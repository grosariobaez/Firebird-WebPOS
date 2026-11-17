namespace JomliaWebPosSync.Configuration;

public sealed class FiscalMappingsOptions
{
    public bool PricesExcludeItbisConfirmed { get; set; }
    public bool NoTipsConfirmed { get; set; }
    public bool PaymentAmountsAreAppliedAmountsConfirmed { get; set; }
    public bool MonetaryLineDiscountConfirmed { get; set; }
    public bool HeaderDiscountEqualsLineSumConfirmed { get; set; }
    public bool LineItbisIsMonetaryAmountConfirmed { get; set; }
    public bool ItbisRoundedPerLineConfirmed { get; set; }
    public bool UseFacFechaVenceAsNcfExpiryConfirmed { get; set; }
    public string Currency { get; set; } = "";
    public decimal CurrencyRate { get; set; }
    public string OperMode { get; set; } = "";
    public int? PaymentType { get; set; }
    public string[] AllowedNcfPrefixes { get; set; } = ["E31", "E32", "E44", "E45", "E46"];
    public string[] AllowedHeaderStatuses { get; set; } = [];
    public string[] AllowedDetailStatuses { get; set; } = [];
    public Dictionary<string, string> DocumentTypes { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, int> TaxCodes { get; set; } = new(StringComparer.Ordinal);
    // Keys company/product and company/customer, with '/' (not ':' used by configuration).
    public Dictionary<string, int> ItemClasses { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> Units { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, CustomerMapping> Customers { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, DateOnly> NcfExpiryDates { get; set; } = new(StringComparer.Ordinal);
    public Dictionary<string, PaymentMapping> PaymentTypes { get; set; } = new(StringComparer.Ordinal);
}

public sealed class CustomerMapping
{
    public string CustomerType { get; set; } = "";
    public string? Ruc { get; set; }
}

public sealed class PaymentMapping
{
    public string WebPosType { get; set; } = "";
    public string? Description { get; set; }
}
