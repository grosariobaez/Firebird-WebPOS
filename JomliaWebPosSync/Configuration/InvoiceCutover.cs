using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Configuration;

public readonly record struct InvoiceCutover(int Company, int InitialId, DateTime Start)
{
    public static InvoiceCutover From(WebPosOptions webpos, SyncOptions sync)
    {
        int company = webpos.SourceCompanyId
            ?? throw new SyncException("SOURCE_COMPANY_UNCONFIGURED", "WebPos.SourceCompanyId");
        if (!webpos.InitialInvoiceIdByCompany.TryGetValue(company, out int initialId))
            throw new SyncException("INITIAL_INVOICE_ID_UNCONFIGURED", "WebPos.InitialInvoiceIdByCompany");
        DateTime start = sync.InvoiceStart
            ?? throw new SyncException("INVOICE_START_UNCONFIGURED", "Sync.InvoiceStart");
        return new(company, initialId, DateTime.SpecifyKind(start, DateTimeKind.Unspecified));
    }

    public bool Contains(InvoiceKey key, DateTime? fecha) =>
        key.IdCompania == Company &&
        key.IdFactura >= InitialId &&
        fecha.HasValue &&
        DateTime.SpecifyKind(fecha.Value, DateTimeKind.Unspecified) >= Start;
}
