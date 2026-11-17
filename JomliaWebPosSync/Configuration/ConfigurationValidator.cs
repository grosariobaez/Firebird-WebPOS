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
        Require(s.SettleDelaySeconds is >= 1 and <= 3600, "Sync:SettleDelaySeconds 1..3600");
        Require(s.ClaimLeaseSeconds is >= 30 and <= 86400, "Sync:ClaimLeaseSeconds 30..86400");
        Require(s.MaxInvoiceRows is >= 1 and <= 100000, "Sync:MaxInvoiceRows 1..100000");
        Require(s.MaxPayloadBytes is >= 4096 and <= 67108864, "Sync:MaxPayloadBytes 4096..67108864");
        Require(s.RetrySettings.InitialRetryDelaySeconds >= 1 && s.RetrySettings.MaxRetryDelaySeconds >= s.RetrySettings.InitialRetryDelaySeconds
            && s.RetrySettings.MaxRetryDelaySeconds <= 86400, "Sync:RetrySettings invalid");
        if (!s.Enabled) return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        var f = firebird.Value; var q = sql.Value; var w = webpos.Value; var m = fiscal.Value;
        static bool Text(string v, int max) => !string.IsNullOrWhiteSpace(v) && v.Length <= max && v == v.Trim();
        Require(Text(f.Host, 255) && Text(f.Database, 1024) && Text(f.Charset, 40), "Firebird:Host/Database/Charset required");
        Require(Text(f.User, 128) && !string.IsNullOrEmpty(f.Password), "Firebird credentials required through protected configuration");
        Require(f.Port is > 0 and <= 65535, "Firebird:Port invalid");
        Require(f.ConnectionTimeoutSeconds is >= 1 and <= 300 && f.CommandTimeoutSeconds is >= 1 and <= 300, "Firebird timeouts 1..300");
        Require(Text(q.Server, 255) && Text(q.Database, 128), "SqlServer:Server/Database required");
        Require(q.IntegratedSecurity || (Text(q.User, 128) && !string.IsNullOrEmpty(q.Password)), "SQL credentials required");
        Require(q.CommandTimeoutSeconds is >= 1 and <= 300 && q.ConnectionTimeoutSeconds is >= 1 and <= 300, "SQL timeouts 1..300");
        Require(s.ClaimLeaseSeconds > 3 * f.CommandTimeoutSeconds, "Claim lease must exceed three Firebird command timeouts");
        Require(w.SourceSystem == "JOMLIA", "WebPos:SourceSystem must be JOMLIA");
        Require(w.SourceCompanyId is >= 0, "WebPos:SourceCompanyId required");
        Require(Text(w.CompanyLicCod, 10) && Text(w.BranchCod, 4) && Text(w.PosCod, 3), "WebPos license/branch/POS required (max 10/4/3 per V6)");
        Require(!w.AllowMonetaryLineDiscount || m.MonetaryLineDiscountConfirmed, "Discount permission requires fiscal confirmation");
        Require(!string.IsNullOrWhiteSpace(s.PayloadDirectory), "Sync:PayloadDirectory required");
        // Unknown fiscal equivalences are checked per document; they do not prevent queue diagnostics.
        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
