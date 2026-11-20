using System.Diagnostics;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Serialization;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Services;

public sealed class SyncCoordinator(ISqlServerRepository sql,
    InvoiceReader reader, InvoiceMapper mapper, InvoiceValidator validator,
    InvoiceJsonSerializer serializer, IPayloadStore store, RetryPolicy retry, SyncDiagnostics diagnostics,
    IOptions<SyncOptions> sync, IOptions<WebPosOptions> webpos, IOptions<SqlServerOptions> destination)
{
    public async Task<bool> ProcessAsync(InvoiceKey key, CancellationToken stopping)
    {
        var timer = Stopwatch.StartNew();
        string? ncf = null;
        try
        {
            if (await sql.IsImportedAsync(key, stopping))
            {
                diagnostics.Invoice("AlreadyImported", key, null, "S", timer.Elapsed);
                return false;
            }
            var payload = await store.LoadAsync(key, stopping);
            if (payload is null)
            {
                var snapshot = await reader.ReadAsync(key, stopping);
                EnsureCutover(key, snapshot);
                ncf = snapshot.Header?.Ncf;
                validator.ValidateSource(snapshot);
                var mapped = mapper.Map(snapshot);
                validator.ValidatePayload(mapped);
                string json = serializer.Serialize(mapped);
                var route = new ImportRoute(webpos.Value.SourceSystem, destination.Value.Server, destination.Value.Database,
                    webpos.Value.CompanyLicCod, webpos.Value.BranchCod, webpos.Value.PosCod, webpos.Value.AllowMonetaryLineDiscount);
                payload = await store.SaveIfAbsentAsync(new(InvoiceJsonSerializer.FormatVersion, key, route,
                    mapped.Header.Ncf, serializer.Fingerprint(snapshot), json,
                    InvoiceJsonSerializer.HashSqlPayload(json), mapper.GetPaymentExpectations(mapped)), stopping);
                diagnostics.Invoice("Prepared", key, ncf, "R", timer.Elapsed);
            }
            else
            {
                ncf = payload.Ncf;
                var snapshot = await reader.ReadAsync(key, stopping);
                EnsureCutover(key, snapshot);
                validator.ValidateReplay(snapshot);
                if (!string.Equals(payload.SourceFingerprint, serializer.Fingerprint(snapshot), StringComparison.Ordinal))
                    throw new SyncException("SOURCE_CHANGED_AFTER_PREPARATION", "snapshot");
            }
            if (await sql.IsImportedAsync(key, stopping))
            {
                diagnostics.Invoice("AlreadyImportedBeforeImport", key, ncf, "S", timer.Elapsed);
                return false;
            }
            await sql.ImportAsync(payload, stopping);
            diagnostics.Invoice("Synchronized", key, ncf, "S", timer.Elapsed);
            diagnostics.Synchronized();
            return true;
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            diagnostics.Invoice("StoppingRecoverable", key, ncf, "R", timer.Elapsed, "CANCELLED", level: LogLevel.Warning);
            return false;
        }
        catch (Exception e)
        {
            var failure = retry.Classify(e);
            if (failure.Transient) diagnostics.Retried(); else diagnostics.Failed();
            diagnostics.Invoice("Failed", key, ncf, failure.Transient ? "R" : "E", timer.Elapsed,
                failure.Code, failure.Field, failure.Transient ? LogLevel.Warning : LogLevel.Error);
            return false;
        }
    }

    private void EnsureCutover(InvoiceKey key, InvoiceSnapshot snapshot)
    {
        if (!InvoiceCutover.From(webpos.Value, sync.Value).Contains(key, snapshot.Header?.Fecha))
            throw new SyncException("OUTSIDE_CUTOVER", "FAC_FECHA/IDFACTURA");
    }

    public async Task<int> ProcessStoredPayloadsAsync(CancellationToken ct)
    {
        int imported = 0;
        foreach (var key in store.EnumerateKeys())
        {
            ct.ThrowIfCancellationRequested();
            if (await sql.IsImportedAsync(key, ct)) continue;
            if (await ProcessAsync(key, ct)) imported++;
        }
        return imported;
    }
}
