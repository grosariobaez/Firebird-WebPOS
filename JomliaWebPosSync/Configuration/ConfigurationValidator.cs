using System.Globalization;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Configuration;

public sealed class ConfigurationValidator(
    IOptions<FirebirdOptions> firebird, IOptions<SqlServerOptions> sql,
    IOptions<WebPosOptions> webpos, IOptions<FiscalMappingsOptions> fiscal) : IValidateOptions<SyncOptions>
{
    public ValidateOptionsResult Validate(string? name, SyncOptions s)
    {
        var errors = new List<string>();
        void Require(bool ok, string field) { if (!ok) errors.Add(field); }
        Require(s.PollIntervalSeconds is >= 1 and <= 3600, "Sync:PollIntervalSeconds 1..3600");
        Require(s.BatchSize is >= 1 and <= 1000, "Sync:BatchSize 1..1000");
        Require(s.MaxInvoiceRows is >= 1 and <= 100000, "Sync:MaxInvoiceRows 1..100000");
        Require(s.MaxPayloadBytes is >= 4096 and <= 67108864, "Sync:MaxPayloadBytes 4096..67108864");
        Require(s.RetrySettings.InitialRetryDelaySeconds >= 1 && s.RetrySettings.MaxRetryDelaySeconds >= s.RetrySettings.InitialRetryDelaySeconds
            && s.RetrySettings.MaxRetryDelaySeconds <= 86400, "Sync:RetrySettings invalid");
        if (!s.Enabled) return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        var f = firebird.Value; var q = sql.Value; var w = webpos.Value; var m = fiscal.Value;
        Require(s.InvoiceStart.HasValue, "Sync:InvoiceStart required (YYYY-MM-DD)");
        static bool Text(string v, int max) => !string.IsNullOrWhiteSpace(v) && v.Length <= max && v == v.Trim();
        Require(Text(f.Host, 255) && Text(f.Database, 1024) && Text(f.Charset, 40), "Firebird:Host/Database/Charset required");
        Require(Text(f.User, 128) && !string.IsNullOrEmpty(f.Password), "Firebird credentials required through protected configuration");
        Require(f.Port is > 0 and <= 65535, "Firebird:Port invalid");
        Require(f.ConnectionTimeoutSeconds is >= 1 and <= 300 && f.CommandTimeoutSeconds is >= 1 and <= 300, "Firebird timeouts 1..300");
        Require(Text(q.Server, 255) && Text(q.Database, 128), "SqlServer:Server/Database required");
        Require(q.IntegratedSecurity || (Text(q.User, 128) && !string.IsNullOrEmpty(q.Password)), "SQL credentials required");
        Require(q.CommandTimeoutSeconds is >= 1 and <= 300 && q.ConnectionTimeoutSeconds is >= 1 and <= 300, "SQL timeouts 1..300");
        Require(w.SourceSystem == "JOMLIA", "WebPos:SourceSystem must be JOMLIA");
        Require(w.SourceCompanyId is >= 0, "WebPos:SourceCompanyId required");
        Require(w.SourceCompanyId.HasValue && w.InitialInvoiceIdByCompany.TryGetValue(w.SourceCompanyId.Value, out int initialId)
            && initialId >= 0, "WebPos:InitialInvoiceIdByCompany[<SourceCompanyId>] required");
        Require(Text(w.CompanyLicCod, 10) && Text(w.BranchCod, 4) && Text(w.PosCod, 3), "WebPos license/branch/POS required (max 10/4/3 per V6)");
        Require(!w.AllowMonetaryLineDiscount || m.MonetaryLineDiscountConfirmed, "Discount permission requires fiscal confirmation");
        Require(m.AllowedNcfPrefixes.Length > 0 && m.AllowedNcfPrefixes.All(p => p is "E31" or "E32" or "E44" or "E45" or "E46"), "FiscalMappings:AllowedNcfPrefixes invalid");
        foreach (var rate in m.ItbisRatesByTaxCode)
        {
            if (!int.TryParse(rate.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int tax) || tax is < 0 or > 4)
            {
                errors.Add($"FiscalMappings:ItbisRatesByTaxCode.{rate.Key} invalid tax code");
                continue;
            }
            decimal canonical = FiscalMappingsOptions.RequiredItbisRate(tax);
            Require(rate.Value == canonical, $"FiscalMappings:ItbisRatesByTaxCode.{rate.Key} must be {canonical:0.##}%");
        }
        Require(m.Currency is "DOP" or "USD" or "EUR", "FiscalMappings:Currency invalid");
        Require(m.CurrencyRate > 0 && (m.Currency != "DOP" || m.CurrencyRate == 1), "FiscalMappings:CurrencyRate invalid");
        Require(m.OperMode is "01" or "02" or "03" or "04" or "05" or "06", "FiscalMappings:OperMode invalid");
        Require(m.PaymentType is null or 1 or 2 or 3, "FiscalMappings:PaymentType invalid");
        Require(!string.IsNullOrWhiteSpace(s.PayloadDirectory), "Sync:PayloadDirectory required");
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
