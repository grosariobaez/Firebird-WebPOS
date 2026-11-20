namespace JomliaWebPosSync.Models;

public sealed record SyncProgress(
    int Version,
    int IdCompania,
    string SourceSystem,
    string Server,
    string Database,
    string CompanyLicCod,
    string BranchCod,
    string PosCod,
    int NextForwardId,
    int ReconcileCursorId,
    int[] CompletedIds,
    int[] RetryIds)
{
    public const int CurrentVersion = 1;

    public static SyncProgress CreateInitial(ImportRoute route, int companyId, int initialId) => new(
        CurrentVersion, companyId, route.SourceSystem, route.Server, route.Database,
        route.CompanyLicCod, route.BranchCod, route.PosCod, initialId, initialId, [], []);

    public bool Matches(ImportRoute route, int companyId) =>
        Version == CurrentVersion && IdCompania == companyId
        && string.Equals(SourceSystem, route.SourceSystem, StringComparison.Ordinal)
        && string.Equals(Server, route.Server, StringComparison.Ordinal)
        && string.Equals(Database, route.Database, StringComparison.Ordinal)
        && string.Equals(CompanyLicCod, route.CompanyLicCod, StringComparison.Ordinal)
        && string.Equals(BranchCod, route.BranchCod, StringComparison.Ordinal)
        && string.Equals(PosCod, route.PosCod, StringComparison.Ordinal);
}
