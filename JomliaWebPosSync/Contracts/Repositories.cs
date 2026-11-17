using JomliaWebPosSync.Models;

namespace JomliaWebPosSync.Contracts;

public interface IFirebirdRepository
{
    Task<IReadOnlyList<PendingInvoice>> GetPendingInvoicesAsync(CancellationToken ct);
    Task<InvoiceClaim?> TryClaimInvoiceAsync(PendingInvoice invoice, CancellationToken ct);
    Task<bool> RenewClaimAsync(InvoiceClaim claim, CancellationToken ct);
    Task<bool> MarkAsSynchronizedAsync(InvoiceClaim claim, CancellationToken ct);
    Task<bool> MarkForRetryAsync(InvoiceClaim claim, int delaySeconds, string error, CancellationToken ct);
    Task<bool> MarkAsErrorAsync(InvoiceClaim claim, string error, CancellationToken ct);
    Task<int> RecoverExpiredClaimsAsync(CancellationToken ct);
    Task<InvoiceSnapshot> ReadSnapshotAsync(InvoiceKey key, CancellationToken ct);
    Task<QueueCounts> GetCountsAsync(CancellationToken ct);
    Task<string?> GetStateAsync(InvoiceKey key, CancellationToken ct);
}

public interface ISqlServerRepository
{
    Task<string> ImportAsync(StoredPayload payload, CancellationToken ct);
}

public interface IPayloadStore
{
    void Initialize();
    IEnumerable<InvoiceKey> EnumerateKeys();
    Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct);
    Task<StoredPayload> SaveIfAbsentAsync(StoredPayload payload, CancellationToken ct);
}
