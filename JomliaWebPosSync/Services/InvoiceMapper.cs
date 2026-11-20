using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Services;

public sealed class InvoiceMapper(IOptions<FiscalMappingsOptions> options, IFiscalDataProvider fiscalData)
{
    public InvoicePayload Map(InvoiceSnapshot source)
    {
        var m = options.Value;
        var h = source.Header ?? throw new SyncException("MISSING_HEADER", "FACTURAS");
        string ncf = h.Ncf ?? throw new SyncException("MISSING_FIELD", "FAC_NCF");
        var customer = CustomerIdentity.Resolve(h);
        DateOnly? expiry = fiscalData.ResolveNcfExpiry(h);

        var billable = source.Details.Where(d => d.Precio is null || d.Precio.Value != 0m).OrderBy(x => x.Secuencia).ToArray();
        if (billable.Length == 0) throw new SyncException("MISSING_DETAILS", "DET_FACTURA");

        var details = billable.Select(d =>
        {
            decimal quantity = Amount(d.Cantidad, "DFC_CANTIDAD");
            decimal price = Amount(d.Precio, "DFC_PRECIO");
            decimal discount = d.Descuento ?? 0m;
            int tax = FiscalItbis.ResolveTax(d.TaxCode); // NULL/vacío/espacios -> 1 (18%); desconocido no vacío -> error
            decimal lineItbis = d.Itbis ?? FiscalItbis.LineAmount(tax, quantity, price, discount);
            var (itemClass, um) = ResolveMeasure(d.Medida);
            return new PayloadDetail(
                d.Secuencia, d.IdProducto ?? throw new SyncException("MISSING_FIELD", "IDPRODUCTO"),
                d.Referencia, d.Descripcion ?? throw new SyncException("MISSING_FIELD", "DFC_DESCRIPCION"),
                quantity, price, discount, lineItbis, d.CantidadDevuelta ?? 0m,
                tax, itemClass, um);
        }).ToArray();

        var payments = source.Payments.OrderBy(x => x.Secuencia).Select(p =>
        {
            string type = p.Tipo ?? throw new SyncException("MISSING_FIELD", "FPA_TIPO");
            var (_, description) = MapPaymentType(type, ncf);
            return new PayloadPayment(p.Secuencia, type, Amount(p.Monto, "FPA_MONTO"), p.Status!, description);
        }).ToArray();

        return new(h.IdCompania, h.IdFactura, new("F", ncf,
            h.Fecha ?? throw new SyncException("MISSING_FIELD", "FAC_FECHA"), expiry,
            customer.Nombre, customer.Ruc, customer.Direccion, customer.CustomerType, 0,
            m.OperMode, m.Currency, m.CurrencyRate, m.PaymentType, 0,
            Amount(h.Total, "FAC_TOTAL"), Amount(h.Itbis, "FAC_ITBIS"), h.Devuelto ?? 0m), details, payments);
    }

    public IReadOnlyList<PaymentExpectation> GetPaymentExpectations(InvoicePayload payload) =>
        payload.Payments.Select(p => p.Tipo).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(t => new PaymentExpectation(t, MapPaymentType(t, payload.Header.Ncf).WebPosType)).ToArray();

    private static (string WebPosType, string? Description) MapPaymentType(string type, string ncf)
    {
        if (string.Equals(type, "F6", StringComparison.Ordinal))
            return ("04", "Credito");
        throw new SyncException("PAYMENT_RULE_UNCONFIRMED", "FPA_TIPO");
    }

    private static (int ItemClass, string Um) ResolveMeasure(string? medida)
    {
        string normalized = (medida ?? string.Empty).Trim().ToUpperInvariant();
        if (normalized == "UND")
            return (0, "UND");
        if (normalized == "SERV")
            throw new SyncException("SERVICE_UNIT_UNCONFIRMED", "MEDIDA");
        if (string.IsNullOrEmpty(normalized))
            throw new SyncException("MISSING_FIELD", "MEDIDA");
        throw new SyncException("UNKNOWN_MEASURE", "MEDIDA");
    }

    private static decimal Amount(decimal? value, string field) => value ?? throw new SyncException("MISSING_FIELD", field);
}
