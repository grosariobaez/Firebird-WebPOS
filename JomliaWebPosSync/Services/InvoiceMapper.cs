using System.Globalization;
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
        Require(m.PricesExcludeItbisConfirmed, "DFC_PRECIO.neto");
        Require(m.NoTipsConfirmed, "propinas");
        Require(m.PaymentAmountsAreAppliedAmountsConfirmed, "FPA_MONTO.aplicado");
        Require(m.EffectiveLineItbisInterpretation is ItbisInterpretation.MonetaryAmount or ItbisInterpretation.PercentageRate, "DFC_ITBIS.estrategia");
        Require(m.ItbisRoundedPerLineConfirmed, "ITBIS.redondeo_por_linea");
        string docType = Lookup(m.DocumentTypes, h.Tipo, "FAC_TIPO");
        string ncf = h.Ncf ?? throw new SyncException("MISSING_FIELD", "FAC_NCF");
        var customer = fiscalData.ResolveCustomer(h.IdCompania, h.IdCliente)
            ?? throw new SyncException("MISSING_MAPPING", "cliente.identificacion");
        DateOnly? expiry = fiscalData.ResolveNcfExpiry(h.IdCompania, ncf, h.FechaVence);
        if (!ncf.StartsWith("E32", StringComparison.Ordinal) && expiry is null)
            throw new SyncException("MISSING_MAPPING", "ncfVencimiento");
        string? address = string.Join(" ", new[] { h.Direccion1, h.Direccion2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (address.Length == 0) address = null;
        var details = source.Details.OrderBy(x => x.Secuencia).Select(d =>
        {
            int tax = Lookup(m.TaxCodes, d.TaxCode, "ITBIS.tax");
            decimal quantity = Amount(d.Cantidad, "DFC_CANTIDAD");
            decimal price = Amount(d.Precio, "DFC_PRECIO");
            decimal discount = Amount(d.Descuento, "DFC_DESCUENTO");
            decimal lineItbis = FiscalItbis.MonetaryAmount(m, tax, quantity, price, discount, d.Itbis);
            return new PayloadDetail(
                d.Secuencia, d.IdProducto ?? throw new SyncException("MISSING_FIELD", "IDPRODUCTO"),
                d.Referencia, d.Descripcion ?? throw new SyncException("MISSING_FIELD", "DFC_DESCRIPCION"),
                quantity, price, discount, lineItbis, Amount(d.CantidadDevuelta, "DFC_CANTDEVUELTA"),
                tax,
                Lookup(m.ItemClasses, Identity(d.IdCompania, d.IdProducto), "IDPRODUCTO.itemClass"),
                Lookup(m.Units, d.Medida, "MEDIDA.um"));
        }).ToArray();
        var payments = source.Payments.OrderBy(x => x.Secuencia).Select(p =>
        {
            var mapping = Lookup(m.PaymentTypes, p.Tipo, "FPA_TIPO");
            Require(mapping.WebPosType is "01" or "02" or "03" or "04" or "05" or "06" or "07" or "08", "FPA_TIPO.WebPosType");
            if (mapping.WebPosType == "05" && !ncf.StartsWith("E32", StringComparison.Ordinal))
                throw new SyncException("GIFT_REQUIRES_E32", "FPA_TIPO");
            return new PayloadPayment(p.Secuencia, p.Tipo!, Amount(p.Monto, "FPA_MONTO"), p.Status!, mapping.Description);
        }).ToArray();
        return new(h.IdCompania, h.IdFactura, new(docType, ncf,
            h.Fecha ?? throw new SyncException("MISSING_FIELD", "FAC_FECHA"), expiry, h.Nombre,
            customer.Ruc, address, customer.CustomerType, 0, m.OperMode, m.Currency, m.CurrencyRate,
            m.PaymentType, 0, Amount(h.Total, "FAC_TOTAL"), Amount(h.Itbis, "FAC_ITBIS"), Amount(h.Devuelto, "FAC_DEVUELTO")), details, payments);
    }

    public IReadOnlyList<PaymentExpectation> GetPaymentExpectations(InvoicePayload payload) =>
        payload.Payments.Select(p => p.Tipo).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Select(t => new PaymentExpectation(t, Lookup(options.Value.PaymentTypes, t, "FPA_TIPO").WebPosType)).ToArray();

    public static string Identity(int company, object? value) => company.ToString(CultureInfo.InvariantCulture) + "/" + Convert.ToString(value, CultureInfo.InvariantCulture);
    private static T Lookup<T>(Dictionary<string, T> map, string? key, string field) =>
        key is not null && map.TryGetValue(key, out var value) ? value : throw new SyncException("MISSING_MAPPING", field);
    private static decimal Amount(decimal? value, string field) => value ?? throw new SyncException("MISSING_FIELD", field);
    private static void Require(bool condition, string field)
    {
        if (!condition) throw new SyncException("FISCAL_RULE_UNCONFIRMED", field);
    }
}
