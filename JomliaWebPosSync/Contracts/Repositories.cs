using JomliaWebPosSync.Models;

namespace JomliaWebPosSync.Contracts;

public interface IFirebirdRepository
{
    Task<IReadOnlyList<InvoiceKey>> GetCandidateInvoicesAsync(CancellationToken ct);
    Task<InvoiceSnapshot> ReadSnapshotAsync(InvoiceKey key, CancellationToken ct);
}

public interface ISqlServerRepository
{
    Task<bool> IsImportedAsync(InvoiceKey key, CancellationToken ct);
    Task<string> ImportAsync(StoredPayload payload, CancellationToken ct);
}

public interface IPayloadStore
{
    void Initialize();
    IEnumerable<InvoiceKey> EnumerateKeys();
    Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct);
    Task<StoredPayload> SaveIfAbsentAsync(StoredPayload payload, CancellationToken ct);
}
