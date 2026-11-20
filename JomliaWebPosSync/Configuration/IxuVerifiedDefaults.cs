using System.Linq;
using System.Text.Json.Nodes;

namespace JomliaWebPosSync.Configuration;

public static class IxuVerifiedDefaults
{
    public static readonly string[] VerifiedAllowedNcfPrefixes = ["E31", "E32", "E44", "E45", "E46"];

    public static JsonObject CreateCanonicalItbisRates() => new()
    {
        ["0"] = 0m,
        ["1"] = 18m,
        ["2"] = 16m,
        ["3"] = 0m,
        ["4"] = 0m
    };

    public static bool ApplyMissing(JsonObject root)
    {
        ArgumentNullException.ThrowIfNull(root);
        var changed = false;

        var webPos = Section(root, "WebPos");
        changed |= SetStringIfMissingOrBlank(webPos, "SourceSystem", "JOMLIA");
        if (webPos["AllowMonetaryLineDiscount"] is null) { webPos["AllowMonetaryLineDiscount"] = false; changed = true; }
        if (webPos["InitialInvoiceIdByCompany"] is null) { webPos["InitialInvoiceIdByCompany"] = new JsonObject(); changed = true; }

        var sync = Section(root, "Sync");
        if (sync["Enabled"] is null) { sync["Enabled"] = false; changed = true; }
        changed |= SetIntIfMissing(sync, "PollIntervalSeconds", 3);
        changed |= SetIntIfMissing(sync, "BatchSize", 10);
        changed |= SetIntIfMissing(sync, "MaxInvoiceRows", 10000);
        changed |= SetIntIfMissing(sync, "MaxPayloadBytes", 16777216);
        changed |= SetStringIfMissingOrBlank(sync, "PayloadDirectory", @"C:\ProgramData\PuntoAparte\JomliaWebPosSync\Outbox");
        var retry = Section(sync, "RetrySettings");
        changed |= SetIntIfMissing(retry, "InitialRetryDelaySeconds", 5);
        changed |= SetIntIfMissing(retry, "MaxRetryDelaySeconds", 300);

        var fiscal = Section(root, "FiscalMappings");
        if (fiscal["AllowedNcfPrefixes"] is not JsonArray prefixes || prefixes.Count == 0)
        {
            fiscal["AllowedNcfPrefixes"] = new JsonArray(VerifiedAllowedNcfPrefixes.Select(x => (JsonNode?)JsonValue.Create(x)).ToArray());
            changed = true;
        }
        if (fiscal["ItbisRatesByTaxCode"] is not JsonObject rates || rates.Count == 0)
        {
            fiscal["ItbisRatesByTaxCode"] = CreateCanonicalItbisRates();
            changed = true;
        }
        changed |= EnsureArray(fiscal, "AllowedHeaderStatuses");
        changed |= EnsureArray(fiscal, "AllowedDetailStatuses");
        foreach (var key in new[] { "DocumentTypes", "TaxCodes", "ItemClasses", "Units", "Customers", "NcfExpiryDates", "PaymentTypes", "PaymentConsistencyRules" })
            changed |= EnsureObject(fiscal, key);
        if (fiscal["LineItbisInterpretation"] is null) { fiscal["LineItbisInterpretation"] = "Unconfirmed"; changed = true; }
        if (fiscal["CurrencyRate"] is null) { fiscal["CurrencyRate"] = 0; changed = true; }
        foreach (var flag in new[] { "PricesExcludeItbisConfirmed", "NoTipsConfirmed", "PaymentAmountsAreAppliedAmountsConfirmed", "MonetaryLineDiscountConfirmed", "HeaderDiscountEqualsLineSumConfirmed", "LineItbisIsMonetaryAmountConfirmed", "ItbisRoundedPerLineConfirmed", "UseFacFechaVenceAsNcfExpiryConfirmed" })
            if (fiscal[flag] is null) { fiscal[flag] = false; changed = true; }

        return changed;
    }

    public static IReadOnlyList<string> GetPendingFiscalItems(JsonObject root)
    {
        var fiscal = root["FiscalMappings"] as JsonObject ?? new JsonObject();
        var pending = new List<string>();
        void Need(bool ok, string item) { if (!ok) pending.Add(item); }

        Need(HasNonEmptyStringArray(fiscal, "AllowedHeaderStatuses"), "Estados de factura (AllowedHeaderStatuses)");
        Need(HasNonEmptyStringArray(fiscal, "AllowedDetailStatuses"), "Estados de detalle (AllowedDetailStatuses)");
        var interp = StringValue(fiscal, "LineItbisInterpretation");
        Need(interp is "MonetaryAmount" or "PercentageRate" or "HeaderAllocated", "Estrategia DFC_ITBIS (LineItbisInterpretation)");
        Need(BoolTrue(fiscal, "PricesExcludeItbisConfirmed"), "Confirmación DFC_PRECIO sin ITBIS");
        Need(BoolTrue(fiscal, "NoTipsConfirmed"), "Confirmación sin propinas");
        Need(BoolTrue(fiscal, "PaymentAmountsAreAppliedAmountsConfirmed"), "Confirmación FPA_MONTO aplicado");
        Need(BoolTrue(fiscal, "ItbisRoundedPerLineConfirmed"), "Confirmación ITBIS redondeado por línea");
        Need(!string.IsNullOrWhiteSpace(StringValue(fiscal, "Currency")), "Moneda (Currency)");
        Need(DecimalValue(fiscal, "CurrencyRate") > 0, "Tasa de cambio (CurrencyRate)");
        Need(!string.IsNullOrWhiteSpace(StringValue(fiscal, "OperMode")), "Modo de operación (OperMode)");
        Need(HasNonEmptyObject(fiscal, "DocumentTypes"), "Tipos de documento (FAC_TIPO -> F)");
        Need(HasNonEmptyObject(fiscal, "TaxCodes"), "Códigos ITBIS/tax");
        Need(HasNonEmptyObject(fiscal, "ItemClasses"), "Clases de artículo");
        Need(HasNonEmptyObject(fiscal, "Units"), "Unidades de medida");
        Need(HasNonEmptyObject(fiscal, "PaymentTypes"), "Tipos de pago (FPA_TIPO)");
        Need(HasNonEmptyObject(fiscal, "PaymentConsistencyRules"), "Reglas de consistencia de pagos");
        if (!HasNonEmptyObject(fiscal, "Customers")) pending.Add("Clientes (Customers), si aplica");
        if (!HasNonEmptyObject(fiscal, "NcfExpiryDates") && !BoolTrue(fiscal, "UseFacFechaVenceAsNcfExpiryConfirmed"))
            pending.Add("Vencimientos eNCF (NcfExpiryDates), salvo E32");
        return pending;
    }

    public static IReadOnlyList<DefaultMatrixRow> BuildMatrix(JsonObject root)
    {
        var fiscal = root["FiscalMappings"] as JsonObject ?? new JsonObject();
        string ncf = string.Join(",", StringArray(fiscal, "AllowedNcfPrefixes"));
        string rates = fiscal["ItbisRatesByTaxCode"] is JsonObject r && r.Count > 0 ? "0=0,1=18,2=16,3=0,4=0" : "(vacío)";
        string sh = string.Join(",", StringArray(fiscal, "AllowedHeaderStatuses"));
        string sd = string.Join(",", StringArray(fiscal, "AllowedDetailStatuses"));
        return
        [
            new("AllowedNcfPrefixes", string.IsNullOrWhiteSpace(ncf) ? "(vacío)" : ncf, "docs/README.md, docs/02_sqlserver_import_v3_QA.sql, ConfigurationValidator (alcance e-CF V3-QA: E31/E32/E44/E45/E46)", "Sí"),
            new("ItbisRatesByTaxCode", rates, "FiscalMappingsOptions.RequiredItbisRate/ConfigurationValidator y especificación WebPOS V6 (tasas 0/3/4=0%, 1=18%, 2=16%)", "Sí"),
            new("Sync.Enabled (instalación nueva)", "false", "Política de activación explícita (plantillas e instalador)", "Sí"),
            new("TLS SQL por defecto", "Encrypt=true, TrustServerCertificate=false", "SqlServerOptions/configuración segura por defecto", "Sí"),
            new("AllowedHeaderStatuses", string.IsNullOrWhiteSpace(sh) ? "(vacío)" : sh, "Sin evidencia verificada en repo/QA; factura 572 observada FAC_STATUS='PEN' (DFC_STATUS NULL, FPA_STATUS='EMI', FPA_TIPO='F6'), pendiente de confirmacion. State=E en logs es error del sincronizador, no FAC_STATUS.", "No"),
            new("AllowedDetailStatuses", string.IsNullOrWhiteSpace(sd) ? "(vacío)" : sd, "Sin evidencia verificada en repo/QA", "No"),
            new("LineItbisInterpretation", StringValue(fiscal, "LineItbisInterpretation"), "Sin evidencia verificada en repo/QA (facturas 568-571 no presentes)", "No"),
            new("Confirmaciones fiscales", "false/pendiente", "Sin evidencia suficiente; no se marca Confirmed=true", "No"),
            new("Currency/CurrencyRate/OperMode/PaymentType", $"{StringValue(fiscal,"Currency")}/{DecimalValue(fiscal,"CurrencyRate")}/{StringValue(fiscal,"OperMode")}/{StringValue(fiscal,"PaymentType")}", "Sin evidencia verificada en repo/QA", "No"),
            new("DocumentTypes/TaxCodes/ItemClasses/Units", "vacío o existente", "Sin mapeos de origen JOMLIA verificados en repo/QA", "No"),
            new("PaymentTypes/PaymentConsistencyRules", "vacío o existente", "Mapeo FPA_TIPO pendiente de datos fuente; sin evidencia verificada", "No"),
            new("Customers/NcfExpiryDates", "vacío o existente", "Dependiente de cliente; sin evidencia verificada", "No"),
        ];
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section) return section;
        section = new JsonObject();
        root[name] = section;
        return section;
    }

    private static bool EnsureArray(JsonObject obj, string key)
    {
        if (obj[key] is JsonArray) return false;
        obj[key] = new JsonArray();
        return true;
    }

    private static bool EnsureObject(JsonObject obj, string key)
    {
        if (obj[key] is JsonObject) return false;
        obj[key] = new JsonObject();
        return true;
    }

    private static bool SetIntIfMissing(JsonObject obj, string key, int value)
    {
        if (obj[key] is not null) return false;
        obj[key] = value;
        return true;
    }

    private static bool SetStringIfMissingOrBlank(JsonObject obj, string key, string value)
    {
        if (obj[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) return false;
        if (obj[key] is not null && obj[key] is not JsonValue) return false;
        obj[key] = value;
        return true;
    }

    private static bool HasNonEmptyStringArray(JsonObject obj, string key) => obj[key] is JsonArray arr && arr.Any(n => n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s));
    private static bool HasNonEmptyObject(JsonObject obj, string key) => obj[key] is JsonObject o && o.Count > 0;
    private static bool BoolTrue(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<bool>(out var b) && b;
    private static string StringValue(JsonObject obj, string key) => obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s ?? "" : obj[key]?.ToJsonString() ?? "";
    private static decimal DecimalValue(JsonObject obj, string key)
    {
        if (obj[key] is not JsonValue v) return 0;
        if (v.TryGetValue<decimal>(out var d)) return d;
        if (v.TryGetValue<double>(out var dd)) return (decimal)dd;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return 0;
    }
    private static IEnumerable<string> StringArray(JsonObject obj, string key)
    {
        if (obj[key] is not JsonArray arr) yield break;
        foreach (var n in arr)
            if (n is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s)) yield return s;
    }
}

public sealed record DefaultMatrixRow(string Field, string Value, string Evidence, string CanBeDefault);