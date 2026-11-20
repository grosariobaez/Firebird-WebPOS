using System.Globalization;

namespace JomliaWebPosSync.Services;

internal static class UnitCatalog
{
    // Transcribed from supplied V6. Ambiguous GRAD must use explicit numeric 16 or 17.
    private static readonly HashSet<string> Abbreviations = new(StringComparer.Ordinal)
    {
        "BARR","BOL","BOT","BULTO","BOTELLA","CAJ","CAJETILLA","CM","CIL","CONJ","CONT",
        "DAÑA","DOC","FARD","GL","GRAN","HOR","HUAC","KG","kWh","LB","LITRO","LOT","M",
        "M2","M3","MMBTU","MIN","PAQ","PAR","PIE","PZA","ROL","SOBR","SEG","TANQUE",
        "TONE","TUB","YD","YD2","UND","EA","MILLAR","SAC","LAT","DIS","BID","RAC","Q",
        "GRT","P2","PAX","PULG","STAY","BDJ","HA","ML","MG","OZ","OZT"
    };
    public static bool IsValid(string unit) => Abbreviations.Contains(unit) ||
        (int.TryParse(unit, NumberStyles.None, CultureInfo.InvariantCulture, out int code) && code is >= 1 and <= 62);

    public static string? Normalize(string? source)
    {
        if (string.IsNullOrWhiteSpace(source)) return null;
        string trimmed = source.Trim();
        if (IsValid(trimmed)) return trimmed;
        string upper = trimmed.ToUpperInvariant();
        if (IsValid(upper)) return upper;
        return null;
    }
}
