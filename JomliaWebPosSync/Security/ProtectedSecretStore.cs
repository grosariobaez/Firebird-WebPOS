using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace JomliaWebPosSync.Security;

public sealed class ProtectedSecretStore
{
    public const string FirebirdPasswordKey = "Firebird:Password";
    public const string SqlServerPasswordKey = "SqlServer:Password";

    private static readonly byte[] Entropy = "JomliaWebPosSync.v1"u8.ToArray();
    private readonly string filePath;
    private readonly bool enforceAcl;

    public ProtectedSecretStore(string? filePath = null, bool enforceAcl = true)
    {
        this.filePath = filePath ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PuntoAparte", "JomliaWebPosSync", "secrets.json");
        this.enforceAcl = enforceAcl;
    }

    public string FilePath => filePath;

    public IReadOnlyDictionary<string, string?> ToConfiguration()
    {
        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var key in new[] { FirebirdPasswordKey, SqlServerPasswordKey })
        {
            var value = Read(key);
            if (!string.IsNullOrEmpty(value)) result[key] = value;
        }
        return result;
    }

    public string? Read(string key)
    {
        if (!File.Exists(filePath)) return null;
        using var document = JsonDocument.Parse(File.ReadAllBytes(filePath));
        if (!document.RootElement.TryGetProperty(key, out var element) || element.ValueKind != JsonValueKind.String)
            return null;
        var cipher = Convert.FromBase64String(element.GetString()!);
        var plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.LocalMachine);
        return Encoding.UTF8.GetString(plain);
    }

    public void Write(IReadOnlyDictionary<string, string?> values)
    {
        var directory = System.IO.Path.GetDirectoryName(filePath)!;
        Directory.CreateDirectory(directory);
        if (enforceAcl) ProtectDirectory(directory);

        var current = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (File.Exists(filePath))
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(filePath));
            foreach (var property in document.RootElement.EnumerateObject())
                if (property.Value.ValueKind == JsonValueKind.String)
                    current[property.Name] = property.Value.GetString();
        }

        foreach (var pair in values)
        {
            if (string.IsNullOrEmpty(pair.Value)) current.Remove(pair.Key);
            else current[pair.Key] = Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(pair.Value), Entropy, DataProtectionScope.LocalMachine));
        }

        var json = JsonSerializer.Serialize(current, new JsonSerializerOptions { WriteIndented = true });
        var temporary = filePath + ".tmp";
        File.WriteAllText(temporary, json, new UTF8Encoding(false));
        if (enforceAcl) ProtectFile(temporary);
        File.Move(temporary, filePath, true);
        if (enforceAcl) ProtectFile(filePath);
    }

    private static void ProtectDirectory(string directory)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in AllowedIdentities())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void ProtectFile(string file)
    {
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        foreach (var sid in AllowedIdentities())
            security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }

    private static SecurityIdentifier[] AllowedIdentities() =>
    [
        new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
    ];
}
