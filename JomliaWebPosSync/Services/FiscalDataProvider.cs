using JomliaWebPosSync.Configuration;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Services;

public interface IFiscalDataProvider
{
    CustomerMapping? ResolveCustomer(int company, int? customerId);
    DateOnly? ResolveNcfExpiry(int company, string ncf, DateTime? documentDate);
}

public sealed class ConfiguredFiscalDataProvider(IOptions<FiscalMappingsOptions> options) : IFiscalDataProvider
{
    public CustomerMapping? ResolveCustomer(int company, int? customerId) =>
        customerId.HasValue && options.Value.Customers.TryGetValue(InvoiceMapper.Identity(company, customerId.Value), out var customer)
            ? customer : null;

    public DateOnly? ResolveNcfExpiry(int company, string ncf, DateTime? documentDate)
    {
        var m = options.Value;
        if (m.NcfExpiryDates.TryGetValue(InvoiceMapper.Identity(company, ncf), out var configured)) return configured;
        return m.UseFacFechaVenceAsNcfExpiryConfirmed && documentDate.HasValue ? DateOnly.FromDateTime(documentDate.Value) : null;
    }
}
