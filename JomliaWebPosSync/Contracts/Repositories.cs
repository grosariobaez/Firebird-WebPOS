using JomliaWebPosSync.Models;

namespace JomliaWebPosSync.Contracts;

public interface IFirebirdRepository
{
    Task<IReadOnlyList<InvoiceKey>> GetCandidateInvoicesAsync(int fromIdInclusive, int? toIdExclusive, CancellationToken ct);
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
    string DirectoryPath { get; }
    IEnumerable<InvoiceKey> EnumeratePendingKeys();
    IEnumerable<InvoiceKey> EnumerateCompletedKeys();
    Task<StoredPayload?> LoadAsync(InvoiceKey key, CancellationToken ct);
    Task<StoredPayload> SaveIfAbsentAsync(StoredPayload payload, CancellationToken ct);
    Task MarkCompletedAsync(InvoiceKey key, CancellationToken ct);
}
