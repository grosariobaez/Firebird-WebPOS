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
        changed |= SetStringIfMissingOrBlank(fiscal, "Currency", "DOP");
        if (fiscal["CurrencyRate"] is null) { fiscal["CurrencyRate"] = 1m; changed = true; }
        changed |= SetStringIfMissingOrBlank(fiscal, "OperMode", "01");

        // Legacy keys removed in 1.0.4 (kept out of defaults): per-client/NCF/product mappings and verified-* flags.
        return changed;
    }

    public static IReadOnlyList<string> GetPendingFiscalItems(JsonObject root)
    {
        var fiscal = root["FiscalMappings"] as JsonObject ?? new JsonObject();
        var pending = new List<string>();
        void Need(bool ok, string item) { if (!ok) pending.Add(item); }

        Need(!string.IsNullOrWhiteSpace(StringValue(fiscal, "Currency")), "Moneda (Currency)");
        Need(DecimalValue(fiscal, "CurrencyRate") > 0, "Tasa de cambio (CurrencyRate)");
        Need(!string.IsNullOrWhiteSpace(StringValue(fiscal, "OperMode")), "Modo de operación (OperMode)");
        return pending.Distinct(StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyList<DefaultMatrixRow> BuildMatrix(JsonObject root)
    {
        var fiscal = root["FiscalMappings"] as JsonObject ?? new JsonObject();
        string ncf = string.Join(",", StringArray(fiscal, "AllowedNcfPrefixes"));
        string rates = fiscal["ItbisRatesByTaxCode"] is JsonObject r && r.Count > 0 ? "0=0,1=18,2=16,3=0,4=0" : "(vacío)";
        return
        [
            new("AllowedNcfPrefixes", string.IsNullOrWhiteSpace(ncf) ? "(vacío)" : ncf, "docs/README.md, docs/02_sqlserver_import_v3_QA.sql (alcance e-CF V3-QA: E31/E32/E44/E45/E46)", "Sí"),
            new("ItbisRatesByTaxCode", rates, "FiscalMappingsOptions.RequiredItbisRate y especificación WebPOS V6 (0/3/4=0%, 1=18%, 2=16%)", "Sí"),
            new("Sync.Enabled (instalación nueva)", "false", "Política de activación explícita", "Sí"),
            new("TLS SQL por defecto", "Encrypt=true, TrustServerCertificate=false", "Configuración segura por defecto", "Sí"),
            new("FAC_STATUS=PEN, DFC_STATUS=NULL, FPA_STATUS=EMI", "reglas verificadas en código", "Facturas 571/572 observadas (State=E en logs es error del sincronizador)", "Sí"),
            new("FPA_TIPO=F6 -> crédito WebPOS 04", "regla verificada en código", "Confirmado JOMLIA; FAC_PAGADO=0 puede coexistir con crédito aplicado", "Sí"),
            new("Cliente desde FACTURAS", "FAC_NOMBRE/FAC_DIRECCION1/FAC_DIRECCION2(normalizado)", "Derivación automática; sin mapeo manual por cliente", "Sí"),
            new("DFC_ITBIS NULL/vacío/espacios", "tax=1 (18%)", "Regla verificada IXU 1.0.4; no convierte códigos desconocidos no vacíos", "Sí"),
            new("MEDIDA UND/SERV", "UND->ItemClass 0,UM UND; SERV no confirmado (error específico)", "DET_FACTURA.MEDIDA Trim+mayúsculas; sin diccionarios configurables", "Parcial"),
            new("ncfVencimiento", "desde FACTURAS.FAC_FECHAVENCE (ISO yyyy-MM-dd)", "Lectura directa; E31/E44/E45/E46 requieren valor, E32 permite NULL", "Sí"),
        ];
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        if (root[name] is JsonObject section) return section;
        section = new JsonObject();
        root[name] = section;
        return section;
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
