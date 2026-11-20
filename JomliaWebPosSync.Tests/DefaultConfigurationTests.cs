using System.Text.Json.Nodes;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Services;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Tests;

public sealed class DefaultConfigurationTests
{
    [Fact]
    public void FreshInstallConfig_PreloadsVerifiedFiscalDefaults_AndKeepsSyncDisabled()
    {
        var root = new JsonObject();

        IxuVerifiedDefaults.ApplyMissing(root);

        var fiscal = Assert.IsType<JsonObject>(root["FiscalMappings"]);
        Assert.Equal(new[] { "E31", "E32", "E44", "E45", "E46" },
            fiscal["AllowedNcfPrefixes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        var rates = Assert.IsType<JsonObject>(fiscal["ItbisRatesByTaxCode"]);
        Assert.Equal(0m, rates["0"]!.GetValue<decimal>());
        Assert.Equal(18m, rates["1"]!.GetValue<decimal>());
        Assert.Equal(16m, rates["2"]!.GetValue<decimal>());
        Assert.Equal(0m, rates["3"]!.GetValue<decimal>());
        Assert.Equal(0m, rates["4"]!.GetValue<decimal>());
        Assert.False(root["Sync"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("JOMLIA", root["WebPos"]!["SourceSystem"]!.GetValue<string>());
        Assert.NotEmpty(IxuVerifiedDefaults.GetPendingFiscalItems(root));
    }

    [Fact]
    public void ApplyMissing_DoesNotOverwriteCustomValues_ExistingValuesPrevail()
    {
        var root = JsonNode.Parse("""
        {
          "Sync": { "Enabled": false },
          "WebPos": { "SourceSystem": "JOMLIA", "CompanyLicCod": "X1" },
          "FiscalMappings": {
            "AllowedNcfPrefixes": [ "E31" ],
            "ItbisRatesByTaxCode": { "1": 18 },
            "AllowedHeaderStatuses": [ "EMI" ],
            "Currency": "USD",
            "CustomFiscal": { "Keep": "yes" }
          }
        }
        """)!.AsObject();

        IxuVerifiedDefaults.ApplyMissing(root);

        var fiscal = root["FiscalMappings"]!.AsObject();
        Assert.Equal(new[] { "E31" }, fiscal["AllowedNcfPrefixes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.True(fiscal["ItbisRatesByTaxCode"]!.AsObject().ContainsKey("1"));
        Assert.Single(fiscal["ItbisRatesByTaxCode"]!.AsObject());
        Assert.Equal(new[] { "EMI" }, fiscal["AllowedHeaderStatuses"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal("USD", fiscal["Currency"]!.GetValue<string>());
        Assert.Equal("yes", fiscal["CustomFiscal"]!["Keep"]!.GetValue<string>());
        Assert.Equal("X1", root["WebPos"]!["CompanyLicCod"]!.GetValue<string>());
    }

    [Fact]
    public void Defaults_DoNotInventOrAuthorizeUnknownStatuses()
    {
        var root = new JsonObject();
        IxuVerifiedDefaults.ApplyMissing(root);
        var fiscal = root["FiscalMappings"]!.AsObject();

        Assert.Empty(fiscal["AllowedHeaderStatuses"]!.AsArray());
        Assert.Empty(fiscal["AllowedDetailStatuses"]!.AsArray());
        Assert.DoesNotContain("ANU", fiscal["AllowedHeaderStatuses"]!.AsArray().Select(n => n?.GetValue<string>()));
        Assert.DoesNotContain("PEN", fiscal["AllowedHeaderStatuses"]!.AsArray().Select(n => n?.GetValue<string>()));

        var options = new FiscalMappingsOptions
        {
            AllowedHeaderStatuses = fiscal["AllowedHeaderStatuses"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray(),
            AllowedDetailStatuses = fiscal["AllowedDetailStatuses"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray()
        };
        var validator = new InvoiceValidator(Options.Create(options), Options.Create(new WebPosOptions { SourceCompanyId = 1 }));
        var ex = Assert.Throws<SyncException>(() => validator.ValidateSource(Invoice572Observed()));
        Assert.Equal("UNCONFIRMED_STATUS", ex.Code);
        Assert.Equal("FAC_STATUS", ex.Field);
    }

    [Fact]
    public void TemplateConfig_ContainsVerifiedDefaultsWithoutIncompleteFiscalDistribution()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "JomliaWebPosSync", "appsettings.json"));
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var fiscal = root["FiscalMappings"]!.AsObject();

        Assert.False(root["Sync"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(new[] { "E31", "E32", "E44", "E45", "E46" },
            fiscal["AllowedNcfPrefixes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(5, fiscal["ItbisRatesByTaxCode"]!.AsObject().Count);
        Assert.Empty(fiscal["AllowedHeaderStatuses"]!.AsArray());
        Assert.Empty(fiscal["DocumentTypes"]!.AsObject());
        Assert.Empty(fiscal["PaymentTypes"]!.AsObject());
    }

    private static InvoiceSnapshot Invoice572Observed() => new(
        new InvoiceKey(1, 572),
        new SourceHeader(1, 572, 1, new DateTime(2026, 1, 1), "Cliente", null, null, 0m, 100m, 100m, 0m, "PEN", "E310000000004", null, "FAC", "31", 0m),
        [new SourceDetail(1, 572, 1, 1, null, "Detalle", 1m, 100m, 0m, 0m, null, 0m, "ITBIS1", "UND")],
        [new SourcePayment(1, 572, 1, "F6", 100m, "EMI", 100m, null, null, null, null, null)]);
}