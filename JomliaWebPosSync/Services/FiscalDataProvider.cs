using JomliaWebPosSync.Configuration;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Services;

public interface IFiscalDataProvider
{
    CustomerMapping? ResolveCustomer(int company, int? customerId, string? ncf);
    DateOnly? ResolveNcfExpiry(int company, string ncf, DateTime? documentDate);
}

public sealed class ConfiguredFiscalDataProvider(IOptions<FiscalMappingsOptions> options) : IFiscalDataProvider
{
    public CustomerMapping? ResolveCustomer(int company, int? customerId, string? ncf)
    {
        if (!string.IsNullOrEmpty(ncf) && options.Value.Customers.TryGetValue(InvoiceMapper.Identity(company, ncf), out var byNcf))
            return byNcf;
        return customerId.HasValue && options.Value.Customers.TryGetValue(InvoiceMapper.Identity(company, customerId.Value), out var byId)
            ? byId : null;
    }

    public DateOnly? ResolveNcfExpiry(int company, string ncf, DateTime? documentDate)
    {
        var m = options.Value;
        if (m.NcfExpiryDates.TryGetValue(InvoiceMapper.Identity(company, ncf), out var configured)) return configured;
        return m.UseFacFechaVenceAsNcfExpiryConfirmed && documentDate.HasValue ? DateOnly.FromDateTime(documentDate.Value) : null;
    }
}
