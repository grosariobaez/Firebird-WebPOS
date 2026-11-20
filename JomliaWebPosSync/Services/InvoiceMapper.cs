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
        string docType = Lookup(m.DocumentTypes, h.Tipo, "FAC_TIPO");
        string ncf = h.Ncf ?? throw new SyncException("MISSING_FIELD", "FAC_NCF");
        var customer = fiscalData.ResolveCustomer(h.IdCompania, h.IdCliente, ncf)
            ?? throw new SyncException("MISSING_MAPPING", "cliente.identificacion");
        DateOnly? expiry = fiscalData.ResolveNcfExpiry(h.IdCompania, ncf, h.FechaVence);
        if (!ncf.StartsWith("E32", StringComparison.Ordinal) && expiry is null)
            throw new SyncException("MISSING_MAPPING", "ncfVencimiento");
        string? address = string.Join(" ", new[] { h.Direccion1, h.Direccion2 }.Where(x => !string.IsNullOrWhiteSpace(x)));
        if (address.Length == 0) address = null;
        var billable = source.Details.Where(d => d.Precio is null || d.Precio.Value != 0m).OrderBy(x => x.Secuencia).ToArray();
        DerivedLineItbis? derived = null;
        if (billable.Any(d => !CanResolveLineItbis(m, d)))
        {
            if (billable.Length == 1)
                derived = DeriveSingleLineFromHeader(m, h, billable[0]);
            else
                throw new SyncException("ITBIS_ALLOCATION_AMBIGUOUS", "DFC_ITBIS");
        }
        else
        {
            Require(m.EffectiveLineItbisInterpretation is ItbisInterpretation.MonetaryAmount or ItbisInterpretation.PercentageRate or ItbisInterpretation.HeaderAllocated, "DFC_ITBIS.estrategia");
            Require(m.ItbisRoundedPerLineConfirmed, "ITBIS.redondeo_por_linea");
        }
        var details = billable.Select(d =>
        {
            decimal quantity = Amount(d.Cantidad, "DFC_CANTIDAD");
            decimal price = Amount(d.Precio, "DFC_PRECIO");
            decimal discount = d.Descuento ?? 0m;
            int tax;
            decimal lineItbis;
            if (derived is not null)
            {
                tax = derived.Value.Tax;
                lineItbis = derived.Value.Itbis;
            }
            else
            {
                tax = Lookup(m.TaxCodes, d.TaxCode, "ITBIS.tax");
                lineItbis = FiscalItbis.MonetaryAmount(m, tax, quantity, price, discount, d.Itbis);
            }
            return new PayloadDetail(
                d.Secuencia, d.IdProducto ?? throw new SyncException("MISSING_FIELD", "IDPRODUCTO"),
                d.Referencia, d.Descripcion ?? throw new SyncException("MISSING_FIELD", "DFC_DESCRIPCION"),
                quantity, price, discount, lineItbis, d.CantidadDevuelta ?? 0m,
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
            m.PaymentType, 0, Amount(h.Total, "FAC_TOTAL"), Amount(h.Itbis, "FAC_ITBIS"), h.Devuelto ?? 0m), details, payments);
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

    private readonly record struct DerivedLineItbis(int Tax, decimal Itbis);

    private static bool CanResolveLineItbis(FiscalMappingsOptions options, SourceDetail detail)
    {
        if (detail.Itbis is not null) return true;
        return options.EffectiveLineItbisInterpretation == ItbisInterpretation.HeaderAllocated
            && !string.IsNullOrWhiteSpace(detail.TaxCode)
            && options.TaxCodes.ContainsKey(detail.TaxCode);
    }

    private static DerivedLineItbis DeriveSingleLineFromHeader(FiscalMappingsOptions options, SourceHeader header, SourceDetail detail)
    {
        if (header.Itbis is null || header.Total is null)
            throw new SyncException("MISSING_FIELD", "FAC_ITBIS");
        decimal quantity = Amount(detail.Cantidad, "DFC_CANTIDAD");
        decimal price = Amount(detail.Precio, "DFC_PRECIO");
        decimal discount = detail.Descuento ?? 0m;
        decimal gross = checked(quantity * price);
        decimal net = FiscalItbis.Round2(gross - discount);
        if (net < 0) throw new SyncException("INVALID_LINE_BASE", "cantidad/precio/descuento");

        int? match = null;
        for (int tax = 0; tax <= 4; tax++)
        {
            decimal rate = FiscalMappingsOptions.RequiredItbisRate(tax);
            decimal itbis = FiscalItbis.Round2(net * rate / 100m);
            if (itbis != header.Itbis.Value) continue;
            if (checked(net + itbis) != header.Total.Value) continue;
            if (tax == 3 && !(header.Ncf ?? string.Empty).StartsWith("E46", StringComparison.Ordinal)) continue;
            if (tax == 4) continue;
            if (match is not null) throw new SyncException("ITBIS_ALLOCATION_AMBIGUOUS", "DFC_ITBIS");
            match = tax;
        }
        if (match is null)
            throw new SyncException("LINE_ITBIS_MISMATCH", "DFC_ITBIS");
        return new DerivedLineItbis(match.Value, header.Itbis.Value);
    }
}
