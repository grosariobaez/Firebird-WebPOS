using System.Diagnostics.Metrics;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Models;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Diagnostics;

public sealed class SyncDiagnostics : IDisposable
{
    private readonly ILogger<SyncDiagnostics> logger;
    private readonly string source;
    private readonly Meter meter = new("JomliaWebPosSync", "1.0");
    private long synchronized, errors, retries;
    public long SynchronizedCount => Interlocked.Read(ref synchronized);
    public long ErrorCount => Interlocked.Read(ref errors);
    public long RetryCount => Interlocked.Read(ref retries);

    public SyncDiagnostics(ILogger<SyncDiagnostics> logger, IOptions<WebPosOptions> options)
    {
        this.logger = logger; source = options.Value.SourceSystem;
        meter.CreateObservableGauge("SynchronizedCount", () => SynchronizedCount);
        meter.CreateObservableGauge("ErrorCount", () => ErrorCount);
        meter.CreateObservableCounter("RetryCount", () => RetryCount);
    }
    public void Synchronized() => Interlocked.Increment(ref synchronized);
    public void Failed() => Interlocked.Increment(ref errors);
    public void Retried() => Interlocked.Increment(ref retries);

    public void Invoice(string action, InvoiceKey key, string? ncf, string state,
        TimeSpan elapsed, string errorType = "None", string field = "", LogLevel level = LogLevel.Information)
    {
        // Do not pass exception messages, customer data or payloads to the logger.
        string? safeNcf = ncf is { Length: 13 } && ncf[0] == 'E' && ncf.AsSpan(1).IndexOfAnyExceptInRange('0', '9') < 0 ? ncf : null;
        logger.Log(level, "{Action} SourceSystem={SourceSystem} IDCOMPANIA={IDCOMPANIA} IDFACTURA={IDFACTURA} NCF={NCF} State={State} DurationMs={DurationMs} ErrorType={ErrorType} Field={Field}",
            action, source, key.IdCompania, key.IdFactura, safeNcf, state, elapsed.TotalMilliseconds, errorType, field);
    }
    public void Dispose() => meter.Dispose();
}
