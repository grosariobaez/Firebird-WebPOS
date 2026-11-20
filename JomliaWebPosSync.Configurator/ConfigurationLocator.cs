using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace JomliaWebPosSync.Configurator;

internal static class ConfigurationLocator
{
    internal const string ServiceName = "JomliaWebPosSync";
    internal const string ConfigFileName = "appsettings.json";
    internal const string LocalConfigFileName = "appsettings.Local.json";
    internal const string ExplicitPathEnvironmentVariable = "JOMLIA_SYNC_CONFIG";

    public static string ResolveEffectiveConfigPath(string? explicitPath = null, string? baseDirectory = null, string? serviceName = ServiceName)
    {
        explicitPath ??= Environment.GetEnvironmentVariable(ExplicitPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(explicitPath))
            return Path.GetFullPath(explicitPath.Trim().Trim('"'));

        var serviceDirectory = ResolveServiceDirectory(serviceName ?? ServiceName);
        if (!string.IsNullOrWhiteSpace(serviceDirectory))
            return Path.Combine(serviceDirectory, ConfigFileName);

        baseDirectory ??= AppContext.BaseDirectory;
        return Path.Combine(GetServiceCandidateDirectory(baseDirectory), ConfigFileName);
    }

    public static string GetServiceCandidateDirectory(string baseDirectory)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(baseDirectory);
        var name = Path.GetFileName(trimmed);
        if (name.Equals("Configurator", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(trimmed)?.FullName;
            if (!string.IsNullOrWhiteSpace(parent)) return parent;
        }
        return trimmed;
    }

    public static string? ResolveServiceDirectory(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            var imagePath = key?.GetValue("ImagePath") as string;
            var exePath = ExtractExecutablePath(imagePath);
            return string.IsNullOrWhiteSpace(exePath) ? null : Path.GetDirectoryName(Path.GetFullPath(exePath));
        }
        catch
        {
            return null;
        }
    }

    public static string? ExtractExecutablePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath)) return null;
        var value = imagePath.Trim();
        if (value.StartsWith('"'))
        {
            var end = value.IndexOf('"', 1);
            if (end > 1) return Environment.ExpandEnvironmentVariables(value[1..end]);
        }

        var expanded = Environment.ExpandEnvironmentVariables(value);
        if (File.Exists(expanded)) return expanded;

        var match = Regex.Match(expanded, @"^[A-Za-z]:\\.*?\.exe\b", RegexOptions.IgnoreCase);
        if (match.Success) return match.Value;

        var firstSpace = expanded.IndexOf(' ');
        return firstSpace > 0 ? expanded[..firstSpace] : expanded;
    }
}
