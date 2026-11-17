using System.Diagnostics;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Serialization;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Services;

public sealed class SyncCoordinator(IFirebirdRepository firebird, ISqlServerRepository sql,
    InvoiceReader reader, InvoiceMapper mapper, InvoiceValidator validator,
    InvoiceJsonSerializer serializer, IPayloadStore store, RetryPolicy retry, SyncDiagnostics diagnostics,
    IOptions<SyncOptions> sync, IOptions<WebPosOptions> webpos, IOptions<SqlServerOptions> destination) : IDisposable
{
    private IEnumerator<InvoiceKey>? recoveryFiles;
    public async Task ProcessAsync(PendingInvoice pending, CancellationToken stopping)
    {
        var timer = Stopwatch.StartNew();
        InvoiceClaim? claim;
        diagnostics.Invoice("Detected", pending.Key, null, pending.Attempts, "P", timer.Elapsed);
        try { claim = await firebird.TryClaimInvoiceAsync(pending, stopping); }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested) { return; }
        catch (Exception e)
        {
            var failure = retry.Classify(e);
            diagnostics.Invoice("ClaimUnavailable", pending.Key, null, pending.Attempts, "P", timer.Elapsed,
                failure.Code, failure.Field, LogLevel.Warning);
            return;
        }
        if (claim is null) return;
        diagnostics.Invoice("Claimed", claim.Key, null, claim.Attempt, "R", timer.Elapsed);
        await using var lease = new ClaimLease(firebird, claim, sync.Value.ClaimLeaseSeconds, stopping);
        string? ncf = null;
        try
        {
            var persisted = await store.LoadAsync(claim.Key, lease.Token);
            var snapshot = await reader.ReadAsync(claim.Key, lease.Token);
            ncf = persisted?.Ncf ?? snapshot.Header?.Ncf;
            var fingerprint = serializer.Fingerprint(snapshot);
            if (persisted is not null)
            {
                // Compare BEFORE revalidation: a later mutation is a conflict, not a new invoice.
                if (!string.Equals(persisted.SourceFingerprint, fingerprint, StringComparison.Ordinal))
                    throw new SyncException("SOURCE_CHANGED_AFTER_PREPARATION", "snapshot");
                // Never remap/reserialize on replay. Even a configuration/software change cannot change the text.
                diagnostics.Invoice("PayloadRecovered", claim.Key, ncf, claim.Attempt, "R", timer.Elapsed);
            }
            else
            {
                validator.ValidateSource(snapshot);
                var payload = mapper.Map(snapshot);
                validator.ValidatePayload(payload);
                string json = serializer.Serialize(payload);
                var route = new ImportRoute(webpos.Value.SourceSystem, destination.Value.Server, destination.Value.Database,
                    webpos.Value.CompanyLicCod, webpos.Value.BranchCod, webpos.Value.PosCod, webpos.Value.AllowMonetaryLineDiscount);
                await lease.EnsureOwnedAsync();
                persisted = await store.SaveIfAbsentAsync(new(InvoiceJsonSerializer.FormatVersion, claim.Key, route,
                    payload.Header.Ncf, fingerprint, json, InvoiceJsonSerializer.HashSqlPayload(json), mapper.GetPaymentExpectations(payload)), lease.Token);
                diagnostics.Invoice("PreparedDurably", claim.Key, ncf, claim.Attempt, "R", timer.Elapsed);
            }
            await lease.EnsureOwnedAsync();
            diagnostics.Invoice("ImportAttempt", claim.Key, ncf, claim.Attempt, "R", timer.Elapsed);
            await sql.ImportAsync(persisted, lease.Token);
            diagnostics.Invoice("SqlConfirmed", claim.Key, ncf, claim.Attempt, "R", timer.Elapsed);
            await lease.StopHeartbeatAsync();
            if (lease.Failure is not null) throw lease.Failure;
            // A trigger reset or expired/replaced claim makes this conditional ACK a no-op.
            bool acknowledged = await firebird.MarkAsSynchronizedAsync(claim, stopping);
            diagnostics.Invoice(acknowledged ? "Synchronized" : "AckDeferredClaimLost", claim.Key, ncf, claim.Attempt,
                acknowledged ? "S" : "Unknown", timer.Elapsed, acknowledged ? "None" : "CLAIM_LOST",
                level: acknowledged ? LogLevel.Information : LogLevel.Warning);
            // Keep the immutable envelope after S: necessary to detect a later trigger resetting S to P.
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            diagnostics.Invoice("StoppingRecoverable", claim.Key, ncf, claim.Attempt, "R", timer.Elapsed, "CANCELLED");
            // Ambiguous SQL outcome is safe: retain payload and let lease expiry recover R.
        }
        catch (Exception e)
        {
            await lease.StopHeartbeatAsync();
            var failure = retry.Classify(lease.Failure ?? e);
            try
            {
                bool updated;
                if (failure.Transient)
                {
                    updated = await firebird.MarkForRetryAsync(claim, retry.DelaySeconds(claim.Attempt), failure.SafeDescription, stopping);
                    if (updated) diagnostics.Retried();
                }
                else updated = await firebird.MarkAsErrorAsync(claim, failure.SafeDescription, stopping);
                diagnostics.Invoice(updated ? (failure.Transient ? "RetryScheduled" : "Blocked") : "StateChangeDeferredClaimLost",
                    claim.Key, ncf, claim.Attempt, updated ? (failure.Transient ? "P" : "E") : "Unknown", timer.Elapsed,
                    failure.Code, failure.Field, failure.Transient ? LogLevel.Warning : LogLevel.Error);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { }
            catch (Exception updateError)
            {
                var updateFailure = retry.Classify(updateError);
                diagnostics.Invoice("StateWriteFailedRecoverable", claim.Key, ncf, claim.Attempt, "Unknown", timer.Elapsed,
                    updateFailure.Code, updateFailure.Field, LogLevel.Error);
            }
        }
    }

    public async Task<bool> InspectStoredPayloadsAsync(CancellationToken ct)
    {
        recoveryFiles ??= store.EnumerateKeys().GetEnumerator();
        // Bounded inventory: retained history must not delay current pending invoices at startup.
        for (int index = 0; index < sync.Value.BatchSize; index++)
        {
            ct.ThrowIfCancellationRequested();
            bool found;
            try { found = recoveryFiles.MoveNext(); }
            catch
            {
                recoveryFiles.Dispose(); recoveryFiles = null;
                throw;
            }
            if (!found)
            {
                recoveryFiles.Dispose(); recoveryFiles = null;
                return true;
            }
            var key = recoveryFiles.Current;
            var timer = Stopwatch.StartNew();
            try
            {
                var payload = await store.LoadAsync(key, ct);
                string? state = await firebird.GetStateAsync(key, ct);
                diagnostics.Invoice(state is null ? "OrphanPayloadRequiresReview" : "StoredPayloadLocated", key, payload?.Ncf, 0,
                    state ?? "Missing", timer.Elapsed, state is null ? "OUTBOX_ROW_MISSING" : "None",
                    level: state is null ? LogLevel.Error : LogLevel.Debug);
                // Queue remains the scheduler: S/E are never automatically reset and absent rows are not fabricated.
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                var failure = retry.Classify(e);
                diagnostics.Invoice("StoredPayloadNeedsReview", key, null, 0, "Unknown", timer.Elapsed, failure.Code, failure.Field, LogLevel.Error);
                // A P row will also be blocked by normal processing, without replacing the corrupt file.
            }
        }
        return false;
    }

    public void Dispose() => recoveryFiles?.Dispose();
}
