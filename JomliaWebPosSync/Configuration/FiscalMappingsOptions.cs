namespace JomliaWebPosSync.Configuration;

public sealed class FiscalMappingsOptions
{
    public static decimal RequiredItbisRate(int tax) => tax switch
    {
        0 or 3 or 4 => 0m,
        1 => 18m,
        2 => 16m,
        _ => throw new ArgumentOutOfRangeException(nameof(tax))
    };

    public bool MonetaryLineDiscountConfirmed { get; set; }
    public bool HeaderDiscountEqualsLineSumConfirmed { get; set; }
    public Dictionary<string, decimal> ItbisRatesByTaxCode { get; set; } = new(StringComparer.Ordinal);
    public string Currency { get; set; } = "DOP";
    public decimal CurrencyRate { get; set; } = 1m;
    public string OperMode { get; set; } = "01";
    public int? PaymentType { get; set; }
    public string[] AllowedNcfPrefixes { get; set; } = ["E31", "E32", "E44", "E45", "E46"];

    public bool IsCanonicalItbisRate(string key, decimal rate)
    {
        if (!int.TryParse(key, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int tax) || tax is < 0 or > 4)
            return false;
        return rate == RequiredItbisRate(tax);
    }
}
