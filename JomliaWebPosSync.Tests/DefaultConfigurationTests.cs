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
    public void FreshInstallConfig_PreloadsVerifiedDefaults_AndKeepsSyncDisabled()
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
        Assert.Equal("DOP", fiscal["Currency"]!.GetValue<string>());
        Assert.Equal(1m, fiscal["CurrencyRate"]!.GetValue<decimal>());
        Assert.Equal("01", fiscal["OperMode"]!.GetValue<string>());
        Assert.False(root["Sync"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal("JOMLIA", root["WebPos"]!["SourceSystem"]!.GetValue<string>());
        Assert.Empty(IxuVerifiedDefaults.GetPendingFiscalItems(root)); // currency/rate/operMode preloaded
    }

    [Fact]
    public void ApplyMissing_DoesNotOverwriteExistingValues_AndPreservesLegacySections()
    {
        var root = JsonNode.Parse("""
        {
          "Sync": { "Enabled": false },
          "WebPos": { "SourceSystem": "JOMLIA", "CompanyLicCod": "X1" },
          "FiscalMappings": {
            "AllowedNcfPrefixes": [ "E31" ],
            "ItbisRatesByTaxCode": { "1": 18 },
            "Currency": "USD",
            "CurrencyRate": 2,
            "OperMode": "02",
            "Customers": { "1/1": { "CustomerType": "03", "Ruc": "123" } },
            "CustomFiscal": { "Keep": "yes" }
          }
        }
        """)!.AsObject();

        IxuVerifiedDefaults.ApplyMissing(root);

        var fiscal = root["FiscalMappings"]!.AsObject();
        Assert.Equal(new[] { "E31" }, fiscal["AllowedNcfPrefixes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Single(fiscal["ItbisRatesByTaxCode"]!.AsObject());
        Assert.Equal("USD", fiscal["Currency"]!.GetValue<string>());
        Assert.Equal(2m, fiscal["CurrencyRate"]!.GetValue<decimal>());
        Assert.Equal("02", fiscal["OperMode"]!.GetValue<string>());
        Assert.Equal("123", fiscal["Customers"]!["1/1"]!["Ruc"]!.GetValue<string>());
        Assert.Equal("yes", fiscal["CustomFiscal"]!["Keep"]!.GetValue<string>());
        Assert.Equal("X1", root["WebPos"]!["CompanyLicCod"]!.GetValue<string>());
    }

    [Fact]
    public void VerifiedStatuses_AreEnforcedInCode_PenNullAccepted_AnuRejected()
    {
        var validator = new InvoiceValidator(Options.Create(new FiscalMappingsOptions()), Options.Create(new WebPosOptions { SourceCompanyId = 1 }));
        validator.ValidateSource(Invoice572ObservedPenNull());

        var anu = Assert.Throws<SyncException>(() => validator.ValidateSource(Invoice572ObservedPenNull() with
        {
            Header = Invoice572ObservedPenNull().Header! with { Status = "ANU" }
        }));
        Assert.Equal("SOURCE_INVOICE_CANCELLED", anu.Code);
    }

    [Fact]
    public void TemplateConfig_ContainsSimplifiedOperationalDefaults()
    {
        var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "JomliaWebPosSync", "appsettings.json"));
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var fiscal = root["FiscalMappings"]!.AsObject();

        Assert.False(root["Sync"]!["Enabled"]!.GetValue<bool>());
        Assert.Equal(new[] { "E31", "E32", "E44", "E45", "E46" },
            fiscal["AllowedNcfPrefixes"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
        Assert.Equal(5, fiscal["ItbisRatesByTaxCode"]!.AsObject().Count);
        Assert.Equal("DOP", fiscal["Currency"]!.GetValue<string>());
        Assert.Null(fiscal["Customers"]);
        Assert.Null(fiscal["NcfExpiryDates"]);
        Assert.Null(fiscal["PaymentTypes"]);
        Assert.Null(fiscal["AllowedHeaderStatuses"]);
    }

    [Fact]
    public void NcfExpiry_ReadsFechaVence_NonE32MissingRejected_E32AllowedNull()
    {
        var provider = new ConfiguredFiscalDataProvider();
        var e32 = new SourceHeader(1, 1, null, new DateTime(2026, 1, 1), "CF", null, null, 0m, 100m, 0m, 0m, "PEN", "E320000000001", null, "FAC", "32", 0m);
        Assert.Null(provider.ResolveNcfExpiry(e32));

        var e31 = e32 with { Ncf = "E310000000004", FechaVence = new DateTime(2026, 12, 4) };
        Assert.Equal(new DateOnly(2026, 12, 4), provider.ResolveNcfExpiry(e31));

        var e31Missing = e31 with { FechaVence = null };
        var ex = Assert.Throws<SyncException>(() => provider.ResolveNcfExpiry(e31Missing));
        Assert.Equal("MISSING_NCF_EXPIRY", ex.Code);
    }

    private static InvoiceSnapshot Invoice572ObservedPenNull() => new(
        new InvoiceKey(1, 572),
        new SourceHeader(1, 572, 1, new DateTime(2026, 1, 1), "Cliente", "Dir", "00112900238", 630m, 4130m, 0m, 0m, "PEN", "E310000000004", new DateTime(2026, 12, 4), "FAC", "31", 0m),
        [new SourceDetail(1, 572, 1, 457, null, "Detalle", 1m, 3500m, 0m, null, null, 0m, null, "UND")],
        [new SourcePayment(1, 572, 1, "F6", 4130m, "EMI", 0m, 0m, 0m, 0m, 4130m, null)]);
}
