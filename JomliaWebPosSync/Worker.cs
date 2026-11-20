using System.Diagnostics;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Services;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync;

public sealed class Worker(IFirebirdRepository firebird, IPayloadStore store, ISyncProgressStore progressStore,
    SyncCoordinator coordinator, SyncDiagnostics diagnostics, RetryPolicy retry, IOptions<SyncOptions> options,
    IOptions<WebPosOptions> webpos, ILogger<Worker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    internal sealed class State
    {
        public required SyncProgress Progress;
        public required HashSet<int> Completed;
        public required HashSet<int> Retry;
        public bool Dirty;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        logger.LogInformation("Service starting Enabled={Enabled} PollSeconds={PollSeconds} BatchSize={BatchSize}",
            o.Enabled, o.PollIntervalSeconds, o.BatchSize);
        if (!o.Enabled)
        {
            logger.LogWarning("Synchronization disabled. No databases or payload directory will be accessed.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            logger.LogInformation("Service stopped");
            return;
        }
        State? state = null;
        try
        {
            store.Initialize();
            progressStore.Initialize(store.DirectoryPath);
            var route = coordinator.CurrentRoute();
            var cutover = InvoiceCutover.From(webpos.Value, o);
            var loaded = await progressStore.LoadAsync(route, cutover.Company, cutover.InitialId, stoppingToken);
            state = BuildState(loaded, route, cutover);
            await RecoverFromOutboxAsync(state, cutover, stoppingToken);
            await PersistAsync(state, stoppingToken);
        }
        catch (Exception e)
        {
            var failure = retry.Classify(e);
            logger.LogCritical("Durable store unavailable ErrorType={ErrorType} Field={Field}", failure.Code, failure.Field);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
            return;
        }

        int pollFailures = 0;
        int cyclesSinceSummary = 0;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int waitSeconds = o.PollIntervalSeconds;
                try
                {
                    var summary = await RunCycleAsync(state!, stoppingToken);
                    pollFailures = 0;
                    cyclesSinceSummary++;
                    bool hasActivity = summary.forwardScanned + summary.reconcileScanned + summary.imported + summary.deferred + summary.failed + summary.completedSkipped > 0;
                    if (hasActivity || cyclesSinceSummary >= 20)
                    {
                        diagnostics.CycleSummary(summary.forwardScanned, summary.reconcileScanned, summary.imported, summary.completedSkipped,
                            summary.deferred, summary.failed, summary.pendingPayloads, state!.Progress.NextForwardId, state.Progress.ReconcileCursorId, summary.elapsed);
                        cyclesSinceSummary = 0;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    var failure = retry.Classify(e);
                    pollFailures = Math.Min(pollFailures + 1, 30);
                    waitSeconds = retry.DelaySeconds(pollFailures);
                    logger.LogError("Polling interrupted; local payloads remain recoverable ErrorType={ErrorType} Field={Field} RetryInSeconds={RetryInSeconds}",
                        failure.Code, failure.Field, waitSeconds);
                }
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            try { if (state is not null) await PersistAsync(state, CancellationToken.None); } catch { }
            logger.LogInformation("Service stopped Synchronized={Synchronized} Errors={Errors} Retries={Retries}",
                diagnostics.SynchronizedCount, diagnostics.ErrorCount, diagnostics.RetryCount);
        }
    }

    internal static State BuildState(SyncProgress? loaded, ImportRoute route, InvoiceCutover cutover)
    {
        var progress = loaded ?? SyncProgress.CreateInitial(route, cutover.Company, cutover.InitialId);
        return new State
        {
            Progress = progress,
            Completed = progress.CompletedIds.ToHashSet(),
            Retry = progress.RetryIds.ToHashSet(),
            Dirty = loaded is null
        };
    }

    internal Task RecoverFromOutboxAsync(State state, InvoiceCutover cutover, CancellationToken ct)
    {
        foreach (var key in store.EnumerateCompletedKeys())
        {
            ct.ThrowIfCancellationRequested();
            if (key.IdCompania != cutover.Company || key.IdFactura < cutover.InitialId) continue;
            if (state.Completed.Add(key.IdFactura)) state.Dirty = true;
            if (state.Retry.Remove(key.IdFactura)) state.Dirty = true;
        }
        foreach (var key in store.EnumeratePendingKeys().OrderBy(k => k.IdFactura))
        {
            ct.ThrowIfCancellationRequested();
            if (key.IdCompania != cutover.Company || key.IdFactura < cutover.InitialId) continue;
            if (state.Completed.Contains(key.IdFactura)) continue;
            if (state.Retry.Add(key.IdFactura)) state.Dirty = true;
        }
        // Advance forward cursor past highest durably-known completed ID to avoid re-scanning completed history on checkpoint loss.
        if (state.Completed.Count > 0)
        {
            int next = state.Completed.Max() + 1;
            if (next > state.Progress.NextForwardId) { state.Progress = state.Progress with { NextForwardId = next }; state.Dirty = true; }
        }
        state.Progress = Snapshot(state.Progress, state);
        return Task.CompletedTask;
    }

    internal async Task<(int forwardScanned, int reconcileScanned, int imported, int completedSkipped, int deferred, int failed, int pendingPayloads, TimeSpan elapsed)> RunCycleAsync(State state, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        int forwardScanned = 0, reconcileScanned = 0, imported = 0, completedSkipped = 0, deferred = 0, failed = 0;
        var cutover = InvoiceCutover.From(webpos.Value, options.Value);

        // 1) Retry/pending known IDs first (bounded), so failures/late-ready payloads don't starve.
        foreach (int id in state.Retry.OrderBy(i => i).Take(options.Value.BatchSize).ToArray())
        {
            ct.ThrowIfCancellationRequested();
            if (state.Completed.Contains(id)) { state.Retry.Remove(id); state.Dirty = true; completedSkipped++; continue; }
            var outcome = await coordinator.ProcessDetailedAsync(new InvoiceKey(cutover.Company, id), ct);
            ApplyOutcome(state, id, outcome, ref imported, ref completedSkipped, ref deferred, ref failed);
        }

        // 2) Forward incremental scan by ID cursor (no OFFSET, no historical reselection).
        var forward = await firebird.GetCandidateInvoicesAsync(state.Progress.NextForwardId, null, ct);
        foreach (var key in forward)
        {
            ct.ThrowIfCancellationRequested();
            forwardScanned++;
            if (state.Completed.Contains(key.IdFactura)) { completedSkipped++; continue; }
            var outcome = await coordinator.ProcessDetailedAsync(key, ct);
            ApplyOutcome(state, key.IdFactura, outcome, ref imported, ref completedSkipped, ref deferred, ref failed);
        }
        if (forward.Count > 0)
        {
            int next = forward.Max(k => k.IdFactura) + 1;
            if (next > state.Progress.NextForwardId)
            {
                state.Progress = state.Progress with { NextForwardId = next };
                state.Dirty = true;
            }
        }

        // 3) Bounded reconciliation over known range to catch late-eligible/past gaps (not full history per cycle).
        if (state.Progress.NextForwardId <= cutover.InitialId)
        {
            state.Progress = state.Progress with { NextForwardId = cutover.InitialId, ReconcileCursorId = cutover.InitialId };
            state.Dirty = true;
        }
        if (state.Progress.ReconcileCursorId >= state.Progress.NextForwardId || state.Progress.ReconcileCursorId < cutover.InitialId)
        {
            state.Progress = state.Progress with { ReconcileCursorId = cutover.InitialId };
            state.Dirty = true;
        }
        int window = Math.Max(options.Value.BatchSize, 1);
        int recFrom = state.Progress.ReconcileCursorId;
        int recTo = Math.Min(recFrom + window, state.Progress.NextForwardId);
        if (recTo > recFrom)
        {
            var rec = await firebird.GetCandidateInvoicesAsync(recFrom, recTo, ct);
            foreach (var key in rec)
            {
                ct.ThrowIfCancellationRequested();
                reconcileScanned++;
                if (state.Completed.Contains(key.IdFactura)) { completedSkipped++; continue; }
                var outcome = await coordinator.ProcessDetailedAsync(key, ct);
                ApplyOutcome(state, key.IdFactura, outcome, ref imported, ref completedSkipped, ref deferred, ref failed);
            }
            state.Progress = state.Progress with { ReconcileCursorId = recTo >= state.Progress.NextForwardId ? cutover.InitialId : recTo };
            state.Dirty = true;
        }

        state.Progress = Snapshot(state.Progress, state);
        await PersistAsync(state, ct);
        sw.Stop();
        return (forwardScanned, reconcileScanned, imported, completedSkipped, deferred, failed, state.Retry.Count, sw.Elapsed);
    }

    private static void ApplyOutcome(State state, int id, ProcessResult result, ref int imported, ref int completedSkipped, ref int deferred, ref int failed)
    {
        switch (result.Disposition)
        {
            case ProcessDisposition.Imported:
                imported++; state.Completed.Add(id); state.Retry.Remove(id); state.Dirty = true; break;
            case ProcessDisposition.Completed:
                completedSkipped++; state.Completed.Add(id); state.Retry.Remove(id); state.Dirty = true; break;
            case ProcessDisposition.Deferred:
                deferred++; if (!state.Completed.Contains(id)) state.Retry.Add(id); state.Dirty = true; break;
            case ProcessDisposition.Rejected:
                failed++; if (!state.Completed.Contains(id)) state.Retry.Add(id); state.Dirty = true; break;
        }
    }

    private static SyncProgress Snapshot(SyncProgress current, State state)
        => current with
        {
            CompletedIds = state.Completed.OrderBy(i => i).ToArray(),
            RetryIds = state.Retry.Where(i => !state.Completed.Contains(i)).OrderBy(i => i).ToArray(),
            NextForwardId = current.NextForwardId,
            ReconcileCursorId = current.ReconcileCursorId
        };

    internal async Task PersistAsync(State state, CancellationToken ct)
    {
        if (!state.Dirty) return;
        state.Progress = Snapshot(state.Progress, state);
        await progressStore.SaveAsync(state.Progress, ct);
        state.Dirty = false;
    }
}
