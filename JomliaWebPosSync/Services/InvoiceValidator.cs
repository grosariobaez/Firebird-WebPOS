using System.Text.RegularExpressions;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Services;

public sealed partial class InvoiceValidator(IOptions<FiscalMappingsOptions> fiscal, IOptions<WebPosOptions> webpos)
{
    [GeneratedRegex("^E(31|32|44|45|46)[0-9]{10}$", RegexOptions.CultureInvariant)]
    private static partial Regex NcfPattern();
    [GeneratedRegex("^[0-9]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Digits();

    public void ValidateSource(InvoiceSnapshot s)
    {
        var h = s.Header ?? throw new SyncException("MISSING_HEADER", "FACTURAS");
        if (string.Equals(h.Status, "ANU", StringComparison.Ordinal))
            throw new SyncException("SOURCE_INVOICE_CANCELLED", "FAC_STATUS");
        Check(s.Key.IdCompania >= 0 && s.Key.IdFactura >= 0 && s.Key.IdCompania == webpos.Value.SourceCompanyId, "SOURCE_IDENTITY", "IDCOMPANIA");
        Check(h.IdCompania == s.Key.IdCompania && h.IdFactura == s.Key.IdFactura, "SOURCE_IDENTITY", "FACTURAS");
        Check(s.Details.Count > 0, "MISSING_DETAILS", "DET_FACTURA");
        Check(s.Payments.Count > 0, "MISSING_PAYMENTS", "FORMAS_PAGO");
        // A partial emission is recoverable, regardless of the number of attempts.
        if (s.Payments.Any(p => p.Status != "EMI")) throw new SyncException("PARTIAL_EMISSION", "FPA_STATUS", true);
        Check(fiscal.Value.AllowedHeaderStatuses.Contains(h.Status, StringComparer.Ordinal), "UNCONFIRMED_STATUS", "FAC_STATUS");
        Check(s.Details.All(d => fiscal.Value.AllowedDetailStatuses.Contains(d.Status, StringComparer.Ordinal)), "UNCONFIRMED_STATUS", "DFC_STATUS");
        Check(s.Details.All(d => d.IdCompania == s.Key.IdCompania && d.IdFactura == s.Key.IdFactura), "SOURCE_IDENTITY", "DET_FACTURA");
        Check(s.Payments.All(p => p.IdCompania == s.Key.IdCompania && p.IdFactura == s.Key.IdFactura), "SOURCE_IDENTITY", "FORMAS_PAGO");
        Check(s.Details.Select(d => d.Secuencia).Distinct().Count() == s.Details.Count, "DUPLICATE_SEQUENCE", "DET_FACTURA");
        Check(s.Payments.Select(p => p.Secuencia).Distinct().Count() == s.Payments.Count, "DUPLICATE_SEQUENCE", "FORMAS_PAGO");
        var total = h.Total ?? throw new SyncException("MISSING_FIELD", "FAC_TOTAL");
        Check(s.Payments.All(p => p.Monto.HasValue), "MISSING_FIELD", "FPA_MONTO");
        Check(s.Payments.Sum(p => p.Monto ?? 0m) == total, "PAYMENT_TOTAL_MISMATCH", "FPA_MONTO/FAC_TOTAL");
        PaymentConsistency.Validate(s, fiscal.Value);
        Check(h.Descuento is >= 0, "INVALID_AMOUNT", "FAC_DESCUENTO");
        if (h.Descuento != 0)
        {
            Check(fiscal.Value.HeaderDiscountEqualsLineSumConfirmed, "FISCAL_RULE_UNCONFIRMED", "FAC_DESCUENTO");
            Check(s.Details.All(d => d.Descuento.HasValue) && s.Details.Sum(d => d.Descuento!.Value) == h.Descuento,
                "DISCOUNT_MISMATCH", "FAC_DESCUENTO");
        }
    }

    public void ValidateReplay(InvoiceSnapshot s)
    {
        var h = s.Header ?? throw new SyncException("MISSING_HEADER", "FACTURAS");
        if (string.Equals(h.Status, "ANU", StringComparison.Ordinal))
            throw new SyncException("CANCELLED_AFTER_PREPARATION", "FAC_STATUS");
    }

    public void ValidatePayload(InvoicePayload p)
    {
        var strategy = fiscal.Value.EffectiveLineItbisInterpretation;
        var h = p.Header;
        Check(h.DocType == "F", "DOCUMENT_TYPE_DISABLED", "docType");
        Check(NcfPattern().IsMatch(h.Ncf), "INVALID_NCF", "ncf");
        Check(fiscal.Value.AllowedNcfPrefixes.Contains(h.Ncf[..3], StringComparer.Ordinal), "DOCUMENT_TYPE_DISABLED", "ncf");
        Check(h.Fecha >= new DateTime(1753, 1, 1) && h.Fecha.Kind == DateTimeKind.Unspecified, "INVALID_DATE", "fecha");
        Check(h.NcfVencimiento.HasValue || h.Ncf.StartsWith("E32", StringComparison.Ordinal), "MISSING_FIELD", "ncfVencimiento");
        Check(h.CustomerType is "03" or "04" or "05" or "06" or "07", "UNKNOWN_FISCAL_CODE", "customerType");
        Length(h.Nombre, 150, "nombre"); Length(h.Direccion, 100, "direccion");
        // V6 is more restrictive (11) than the QA SP (20): do not silently expand the contract.
        Length(h.Ruc, 11, "ruc");
        if (h.CustomerType != "07")
            Check(!string.IsNullOrWhiteSpace(h.Nombre) && !string.IsNullOrWhiteSpace(h.Ruc) && !string.IsNullOrWhiteSpace(h.Direccion), "MISSING_CUSTOMER_DATA", "cliente");
        else Check(h.Ncf.StartsWith("E32", StringComparison.Ordinal), "CUSTOMER_TYPE_REQUIRES_E32", "customerType");
        if (h.CustomerType == "04") Check(h.Ruc is { Length: 9 } && Digits().IsMatch(h.Ruc), "INVALID_IDENTIFICATION_FORMAT", "ruc");
        if (h.CustomerType == "05") Check(h.Ruc is { Length: 11 } && Digits().IsMatch(h.Ruc), "INVALID_IDENTIFICATION_FORMAT", "ruc");
        Check(h.TaxMode == 0, "TAX_MODE_DISABLED", "taxMode");
        Check(h.OperMode is "01" or "02" or "03" or "04" or "05" or "06", "UNKNOWN_FISCAL_CODE", "operMode");
        Check(h.Currency is "DOP" or "USD" or "EUR", "UNKNOWN_FISCAL_CODE", "currency");
        Number(h.CurrencyRate, 3, 4, "currencyRate");
        Check(h.CurrencyRate > 0 && (h.Currency != "DOP" || h.CurrencyRate == 1), "INVALID_CURRENCY_RATE", "currencyRate");
        Check(h.PaymentType is null or 1 or 2 or 3, "UNKNOWN_FISCAL_CODE", "paymentType");
        Check(h.Tip == 0 && h.Devuelto == 0, "RETURNS_OR_TIPS_DISABLED", "tip/devuelto");
        Number(h.Total, 14, 2, "total"); Number(h.Itbis, 14, 2, "itbis");
        decimal baseTotal = 0, taxTotal = 0, paymentTotal = 0;
        foreach (var d in p.Details)
        {
            Check(d.Secuencia >= 0 && d.IdProducto >= 0, "INVALID_DETAIL_IDENTITY", "secuencia/idProducto");
            Length(d.Referencia, 35, "referencia"); Length(d.Descripcion, 80, "descripcion"); Length(d.Um, 10, "um");
            Check(!string.IsNullOrWhiteSpace(d.Descripcion) && UnitCatalog.IsValid(d.Um), "MISSING_OR_UNKNOWN_DETAIL_DATA", "descripcion/um");
            Number(d.Cantidad, 14, 2, "cantidad"); Number(d.Precio, 14, 4, "precio");
            Number(d.Descuento, 14, 2, "descuento"); Number(d.Itbis, 14, 2, "itbis.linea");
            Check(d.Cantidad > 0 && d.CantidadDevuelta == 0, "INVALID_QUANTITY_OR_RETURN", "cantidad/cantidadDevuelta");
            Check(d.Tax is >= 0 and <= 4 && d.ItemClass is 0 or 1, "UNKNOWN_FISCAL_CODE", "tax/itemClass");
            Check(d.Tax != 3 || h.Ncf.StartsWith("E46", StringComparison.Ordinal), "ZERO_TAX_REQUIRES_E46", "tax");
            decimal gross = checked(d.Cantidad * d.Precio);
            Check(d.Descuento <= Round(gross), "INVALID_DISCOUNT", "descuento");
            if (d.Descuento != 0)
                Check(webpos.Value.AllowMonetaryLineDiscount && fiscal.Value.MonetaryLineDiscountConfirmed, "DISCOUNT_DISABLED", "DFC_DESCUENTO");
            decimal net = Round(gross - d.Descuento);
            Check(net >= 0, "INVALID_LINE_BASE", "cantidad/precio/descuento");
            Check((d.Precio != 0 && d.Descuento != Round(gross)) || d.Tax == 4, "ZERO_LINE_REQUIRES_NONBILLABLE", "tax");
            Check(d.Tax != 4 || net == 0, "NONBILLABLE_AMOUNT_UNCONFIRMED", "tax");
            decimal rate = strategy == ItbisInterpretation.PercentageRate
                ? FiscalItbis.PercentageForTaxCode(fiscal.Value, d.Tax) / 100m
                : FiscalMappingsOptions.RequiredItbisRate(d.Tax) / 100m;
            Check(d.Itbis == Round(net * rate), "LINE_ITBIS_MISMATCH", "DFC_ITBIS");
            baseTotal = checked(baseTotal + net); taxTotal = checked(taxTotal + d.Itbis);
        }
        foreach (var pay in p.Payments)
        {
            Check(pay.Secuencia >= 0 && pay.Estatus == "EMI" && !string.IsNullOrWhiteSpace(pay.Tipo), "INVALID_PAYMENT", "payments");
            Length(pay.Tipo, 3, "tipo"); Length(pay.Descripcion, 20, "descripcion.pago");
            Number(pay.Monto, 14, 2, "monto"); paymentTotal = checked(paymentTotal + pay.Monto);
        }
        Check(taxTotal == h.Itbis, "HEADER_ITBIS_MISMATCH", "FAC_ITBIS");
        Check(baseTotal + h.Itbis == h.Total, "TOTAL_MISMATCH", "FAC_TOTAL");
        Check(paymentTotal == h.Total, "PAYMENT_TOTAL_MISMATCH", "FPA_MONTO");
    }

    private static decimal Round(decimal value) => FiscalItbis.Round2(value);
    private static void Length(string? value, int max, string field) => Check(value is null || value.Length <= max, "TEXT_TOO_LONG", field);
    private static void Number(decimal value, int integerDigits, int scale, string field)
    {
        decimal bound = 1; for (int i = 0; i < integerDigits; i++) bound *= 10;
        Check(value >= 0 && value < bound && decimal.Round(value, scale) == value, "DECIMAL_OUT_OF_CONTRACT", field);
    }
    private static void Check(bool ok, string code, string field)
    {
        if (!ok) throw new SyncException(code, field);
    }
}
