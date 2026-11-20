using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Services;

public interface IFiscalDataProvider
{
    DateOnly? ResolveNcfExpiry(SourceHeader header);
}

public sealed class ConfiguredFiscalDataProvider : IFiscalDataProvider
{
    public DateOnly? ResolveNcfExpiry(SourceHeader header)
    {
        string ncf = header.Ncf ?? string.Empty;
        bool isE32 = ncf.StartsWith("E32", StringComparison.OrdinalIgnoreCase);
        if (header.FechaVence is null)
        {
            if (isE32) return null;
            throw new SyncException("MISSING_NCF_EXPIRY", "FAC_FECHAVENCE");
        }
        return DateOnly.FromDateTime(header.FechaVence.Value);
    }
}
