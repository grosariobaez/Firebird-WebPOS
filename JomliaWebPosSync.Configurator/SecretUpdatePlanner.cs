using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Security;

namespace JomliaWebPosSync.Configurator;

internal static class SecretUpdatePlanner
{
    public static IReadOnlyDictionary<string, string?> BuildUpdates(string? firebirdPasswordInput, string? sqlPasswordInput, bool sqlIntegratedSecurity, ProtectedSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return BuildUpdates(firebirdPasswordInput, sqlPasswordInput, sqlIntegratedSecurity, store.Read);
    }

    public static IReadOnlyDictionary<string, string?> BuildUpdates(string? firebirdPasswordInput, string? sqlPasswordInput, bool sqlIntegratedSecurity, Func<string, string?> readSecret)
    {
        ArgumentNullException.ThrowIfNull(readSecret);
        var updates = new Dictionary<string, string?>(StringComparer.Ordinal);
        Plan(updates, ProtectedSecretStore.FirebirdPasswordKey, firebirdPasswordInput, readSecret);
        if (sqlIntegratedSecurity)
            updates[ProtectedSecretStore.SqlServerPasswordKey] = null;
        else
            Plan(updates, ProtectedSecretStore.SqlServerPasswordKey, sqlPasswordInput, readSecret);
        return updates;
    }

    public static string ResolveForTest(string? input, string key, ProtectedSecretStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        return ResolveForTest(input, key, store.Read);
    }

    public static string ResolveForTest(string? input, string key, Func<string, string?> readSecret)
    {
        ArgumentNullException.ThrowIfNull(readSecret);
        if (!string.IsNullOrEmpty(input)) return input;
        return readSecret(key) ?? string.Empty;
    }

    private static void Plan(IDictionary<string, string?> updates, string key, string? input, Func<string, string?> readSecret)
    {
        if (!string.IsNullOrEmpty(input))
        {
            updates[key] = input;
            return;
        }

        if (!string.IsNullOrEmpty(readSecret(key))) return;
        updates[key] = null;
    }
}
