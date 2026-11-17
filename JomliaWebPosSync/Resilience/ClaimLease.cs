using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Models;

namespace JomliaWebPosSync.Resilience;

public sealed class ClaimLease : IAsyncDisposable
{
    private readonly IFirebirdRepository repository;
    private readonly InvoiceClaim claim;
    private readonly CancellationTokenSource operation;
    private readonly CancellationTokenSource heartbeat;
    private readonly Task renewal;
    private readonly SemaphoreSlim renewalGate = new(1, 1);
    public CancellationToken Token => operation.Token;
    public Exception? Failure { get; private set; }

    public ClaimLease(IFirebirdRepository repository, InvoiceClaim claim, int leaseSeconds, CancellationToken stopping)
    {
        this.repository = repository; this.claim = claim;
        operation = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        heartbeat = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        renewal = RunAsync(TimeSpan.FromSeconds(leaseSeconds / 3d));
    }

    private async Task RunAsync(TimeSpan interval)
    {
        try
        {
            while (true)
            {
                await Task.Delay(interval, heartbeat.Token);
                if (!await RenewAsync(heartbeat.Token))
                    throw new SyncException("CLAIM_LOST", "PA_FB_OUTBOX", true);
            }
        }
        catch (OperationCanceledException) when (heartbeat.IsCancellationRequested) { }
        catch (Exception e)
        {
            Failure = e;
            await operation.CancelAsync();
        }
    }

    public async Task EnsureOwnedAsync()
    {
        Token.ThrowIfCancellationRequested();
        if (!await RenewAsync(Token))
            throw new SyncException("CLAIM_LOST", "PA_FB_OUTBOX", true);
    }

    private async Task<bool> RenewAsync(CancellationToken ct)
    {
        await renewalGate.WaitAsync(ct);
        try { return await repository.RenewClaimAsync(claim, ct); }
        finally { renewalGate.Release(); }
    }

    public async Task StopHeartbeatAsync()
    {
        await heartbeat.CancelAsync();
        await renewal;
    }

    public async ValueTask DisposeAsync()
    {
        await StopHeartbeatAsync();
        heartbeat.Dispose(); operation.Dispose(); renewalGate.Dispose();
    }
}
