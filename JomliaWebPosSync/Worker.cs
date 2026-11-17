using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Services;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync;

public sealed class Worker(IFirebirdRepository firebird, IPayloadStore store, SyncCoordinator coordinator,
    SyncDiagnostics diagnostics, RetryPolicy retry, IOptions<SyncOptions> options,
    ILogger<Worker> logger, IHostApplicationLifetime lifetime) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var o = options.Value;
        logger.LogInformation("Service starting Enabled={Enabled} PollSeconds={PollSeconds} BatchSize={BatchSize} LeaseSeconds={LeaseSeconds} Processors=1",
            o.Enabled, o.PollIntervalSeconds, o.BatchSize, o.ClaimLeaseSeconds);
        if (!o.Enabled)
        {
            logger.LogWarning("Synchronization disabled. No databases or payload directory will be accessed.");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
            logger.LogInformation("Service stopped");
            return;
        }
        try { store.Initialize(); }
        catch (Exception e)
        {
            var failure = retry.Classify(e);
            logger.LogCritical("Durable store unavailable ErrorType={ErrorType} Field={Field}", failure.Code, failure.Field);
            Environment.ExitCode = 1;
            lifetime.StopApplication();
            return;
        }
        bool inspected = false;
        bool connectionHealthy = false;
        int pollFailures = 0;
        DateTimeOffset nextMetrics = DateTimeOffset.MinValue;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                int waitSeconds = o.PollIntervalSeconds;
                try
                {
                    int recovered = await firebird.RecoverExpiredClaimsAsync(stoppingToken);
                    if (recovered > 0) logger.LogInformation("Expired claims recovered Count={Count}", recovered);
                    if (!connectionHealthy) logger.LogInformation("Firebird connection available");
                    connectionHealthy = true;
                    var pending = await firebird.GetPendingInvoicesAsync(stoppingToken);
                    foreach (var invoice in pending)
                    {
                        stoppingToken.ThrowIfCancellationRequested();
                        await coordinator.ProcessAsync(invoice, stoppingToken);
                    }
                    try
                    {
                        int reconciled = await coordinator.ProcessSourceEventsAsync(stoppingToken);
                        if (reconciled > 0) logger.LogInformation("Source events reconciled Count={Count}", reconciled);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                    catch (Exception e)
                    {
                        var failure = retry.Classify(e);
                        logger.LogWarning("Source event reconciliation deferred ErrorType={ErrorType} Field={Field}", failure.Code, failure.Field);
                    }
                    if (!inspected) inspected = await coordinator.InspectStoredPayloadsAsync(stoppingToken);
                    if (DateTimeOffset.UtcNow >= nextMetrics)
                    {
                        diagnostics.UpdateCounts(await firebird.GetCountsAsync(stoppingToken));
                        logger.LogInformation("Queue PendingCount={PendingCount} ProcessingCount={ProcessingCount} SynchronizedCount={SynchronizedCount} ErrorCount={ErrorCount} RetryCount={RetryCount}",
                            diagnostics.PendingCount, diagnostics.ProcessingCount, diagnostics.SynchronizedCount, diagnostics.ErrorCount, diagnostics.RetryCount);
                        nextMetrics = DateTimeOffset.UtcNow.AddMinutes(1);
                    }
                    pollFailures = 0;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception e)
                {
                    connectionHealthy = false;
                    var failure = retry.Classify(e);
                    pollFailures = Math.Min(pollFailures + 1, 30);
                    waitSeconds = retry.DelaySeconds(pollFailures);
                    logger.LogError("Polling interrupted; queue remains recoverable ErrorType={ErrorType} Field={Field} RetryInSeconds={RetryInSeconds}",
                        failure.Code, failure.Field, waitSeconds);
                }
                await Task.Delay(TimeSpan.FromSeconds(waitSeconds), stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally { logger.LogInformation("Service stopped; unacknowledged claims remain recoverable"); }
    }
}
