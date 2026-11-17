using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Models;

namespace JomliaWebPosSync.Services;

public sealed class InvoiceReader(IFirebirdRepository repository)
{
    public Task<InvoiceSnapshot> ReadAsync(InvoiceKey key, CancellationToken ct) => repository.ReadSnapshotAsync(key, ct);
}
