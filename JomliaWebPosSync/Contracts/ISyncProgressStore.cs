using JomliaWebPosSync.Models;

namespace JomliaWebPosSync.Contracts;

public interface ISyncProgressStore
{
    void Initialize(string directory);
    Task<SyncProgress?> LoadAsync(ImportRoute route, int companyId, int initialId, CancellationToken ct);
    Task SaveAsync(SyncProgress progress, CancellationToken ct);
}
