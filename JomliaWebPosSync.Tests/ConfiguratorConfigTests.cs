using System.Text.Json.Nodes;
using JomliaWebPosSync.Configurator;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Security;

namespace JomliaWebPosSync.Tests;

public sealed class ConfiguratorConfigTests
{
    [Fact]
    public void ResolveEffectiveConfigPath_UsesServiceInstallDirectory_WhenConfiguratorLivesInSubfolder()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "IXU-" + Guid.NewGuid().ToString("N"), "Configurator") + Path.DirectorySeparatorChar;
        var path = ConfigurationLocator.ResolveEffectiveConfigPath(null, baseDir, "ServiceThatDoesNotExist_IXUTest");
        Assert.Equal(Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(baseDir))!, "appsettings.json"), path);
    }

    [Fact]
    public void ResolveEffectiveConfigPath_UsesExplicitEnvironmentOverride()
    {
        var expected = Path.Combine(Path.GetTempPath(), "ixu-explicit-" + Guid.NewGuid().ToString("N"), "appsettings.json");
        var path = ConfigurationLocator.ResolveEffectiveConfigPath(expected, AppContext.BaseDirectory, "ignored");
        Assert.Equal(Path.GetFullPath(expected), path);
    }

    [Fact]
    public void ExtractExecutablePath_ParsesQuotedScImagePath()
    {
        var exe = ConfigurationLocator.ExtractExecutablePath("\"C:\\Program Files\\JomliaWebPosSync\\JomliaWebPosSync.exe\"");
        Assert.Equal(@"C:\Program Files\JomliaWebPosSync\JomliaWebPosSync.exe", exe);
    }

    [Fact]
    public void LoadOrCreate_PreservesUneditedSections_AndLegacyKeys_DuringUpdate()
    {
        var path = Path.Combine(Path.GetTempPath(), "ixu-cfg-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, """
        {
          "Firebird": { "Host": "FB-HOST", "Port": 3050 },
          "SqlServer": { "Server": "OLD-SQL", "Database": "DB" },
          "FiscalMappings": { "Currency": "DOP", "Customers": { "1/1": {"CustomerType":"03"} }, "Custom": { "Keep": true } },
          "Sync": { "Enabled": false }
        }
        """);
        var root = JsonConfigurationEditor.LoadOrCreate(path);
        JsonConfigurationEditor.Set(root, "SqlServer", "Server", "NEW-SQL");
        IxuVerifiedDefaults.ApplyMissing(root); // migration safe: fills missing, keeps existing/legacy
        Assert.Equal("FB-HOST", root["Firebird"]?["Host"]?.GetValue<string>());
        Assert.Equal("NEW-SQL", root["SqlServer"]?["Server"]?.GetValue<string>());
        Assert.Equal("03", root["FiscalMappings"]?["Customers"]?["1/1"]?["CustomerType"]?.GetValue<string>());
        Assert.True(root["FiscalMappings"]?["Custom"]?["Keep"]?.GetValue<bool>());
        Assert.False(root["Sync"]?["Enabled"]?.GetValue<bool>());
    }

    [Fact]
    public void MergeCompanyInitialInvoiceId_PreservesOtherCompanies()
    {
        var webPos = new JsonObject { ["InitialInvoiceIdByCompany"] = new JsonObject { ["2"] = 50, ["7"] = 99 } };
        JsonConfigurationEditor.MergeCompanyInitialInvoiceId(webPos, 2, 250);
        var map = Assert.IsType<JsonObject>(webPos["InitialInvoiceIdByCompany"]);
        Assert.Equal(250, map["2"]?.GetValue<int>());
        Assert.Equal(99, map["7"]?.GetValue<int>());
    }

    [Fact]
    public void SecretUpdatePlanner_PreservesExistingProtectedSecrets_WhenInputBlank()
    {
        var existing = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ProtectedSecretStore.FirebirdPasswordKey] = "fb-secret",
            [ProtectedSecretStore.SqlServerPasswordKey] = "sql-secret"
        };
        string? Read(string key) => existing.TryGetValue(key, out var value) ? value : null;
        var updates = SecretUpdatePlanner.BuildUpdates(string.Empty, string.Empty, false, Read);
        Assert.Empty(updates);
        Assert.Equal("fb-secret", SecretUpdatePlanner.ResolveForTest(string.Empty, ProtectedSecretStore.FirebirdPasswordKey, Read));
        Assert.Equal("sql-secret", SecretUpdatePlanner.ResolveForTest(string.Empty, ProtectedSecretStore.SqlServerPasswordKey, Read));
    }

    [Fact]
    public void SecretUpdatePlanner_UpdatesOnlyChangedPassword_AndClearsSqlSecretForIntegratedAuth()
    {
        var existing = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [ProtectedSecretStore.FirebirdPasswordKey] = "fb-secret",
            [ProtectedSecretStore.SqlServerPasswordKey] = "sql-secret"
        };
        string? Read(string key) => existing.TryGetValue(key, out var value) ? value : null;
        var updates = SecretUpdatePlanner.BuildUpdates("fb-new", string.Empty, true, Read);
        Assert.Equal("fb-new", updates[ProtectedSecretStore.FirebirdPasswordKey]);
        Assert.True(updates.ContainsKey(ProtectedSecretStore.SqlServerPasswordKey));
        Assert.Null(updates[ProtectedSecretStore.SqlServerPasswordKey]);
    }

    [Fact]
    public void LoadOrCreate_ReturnsEmpty_WhenConfigMissing()
    {
        var missing = Path.Combine(Path.GetTempPath(), "ixu-missing-" + Guid.NewGuid().ToString("N"), "appsettings.json");
        var root = JsonConfigurationEditor.LoadOrCreate(missing);
        JsonConfigurationEditor.Set(root, "SqlServer", "Server", "SQL1");
        Assert.False(File.Exists(missing));
        Assert.Equal("SQL1", root["SqlServer"]?["Server"]?.GetValue<string>());
    }
}
