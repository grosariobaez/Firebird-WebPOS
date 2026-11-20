using System.Text.RegularExpressions;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Services;

public sealed record ResolvedCustomer(string? Nombre, string? Direccion, string? Ruc, string CustomerType);

internal static class CustomerIdentity
{
    private static readonly Regex DigitsOnly = new("^[0-9]+$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static ResolvedCustomer Resolve(SourceHeader header)
    {
        string? nombre = NormalizeText(header.Nombre);
        string? direccion = NormalizeText(header.Direccion1);
        string? rawId = NormalizeDigits(header.Direccion2);
        string ncf = header.Ncf ?? string.Empty;

        if (IsFinalConsumer(rawId, nombre, ncf))
        {
            if (string.IsNullOrWhiteSpace(nombre)) nombre = "Consumidor Final";
            return new ResolvedCustomer(nombre, direccion, null, "07");
        }

        if (string.IsNullOrWhiteSpace(rawId))
            throw new SyncException("MISSING_CUSTOMER_IDENTIFICATION", "FAC_DIRECCION2");

        if (rawId.Length == 9)
            return new ResolvedCustomer(nombre, direccion, rawId, "04");
        if (rawId.Length == 11)
            return new ResolvedCustomer(nombre, direccion, rawId, "05");

        throw new SyncException("INVALID_IDENTIFICATION_FORMAT", "FAC_DIRECCION2");
    }

    private static bool IsFinalConsumer(string? id, string? nombre, string ncf)
    {
        if (string.IsNullOrWhiteSpace(id))
            return ncf.StartsWith("E32", StringComparison.Ordinal)
                || string.Equals(nombre, "CONSUMIDOR FINAL", StringComparison.OrdinalIgnoreCase)
                || string.Equals(nombre, "CONSUMIDOR", StringComparison.OrdinalIgnoreCase);
        return id.All(c => c == '0');
    }

    private static string? NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static string? NormalizeDigits(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var chars = value.Where(char.IsDigit).ToArray();
        if (chars.Length == 0) return null;
        var digits = new string(chars);
        if (!DigitsOnly.IsMatch(digits)) return null;
        if (digits.All(c => c == '0')) return null;
        return digits;
    }
}
