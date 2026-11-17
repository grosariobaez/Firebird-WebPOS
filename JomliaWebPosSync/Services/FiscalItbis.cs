using System.Globalization;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Services;

internal static class FiscalItbis
{
    public static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static decimal PercentageForTaxCode(FiscalMappingsOptions options, int tax)
    {
        string key = tax.ToString(CultureInfo.InvariantCulture);
        if (!options.ItbisRatesByTaxCode.TryGetValue(key, out decimal rate) || rate is < 0 or > 100)
            throw new SyncException("MISSING_MAPPING", "ITBIS.rate");
        return rate;
    }

    public static decimal MonetaryAmount(FiscalMappingsOptions options, int tax, decimal quantity, decimal price, decimal discount, decimal? sourceItbis)
    {
        var strategy = options.EffectiveLineItbisInterpretation;
        if (strategy == ItbisInterpretation.MonetaryAmount)
        {
            decimal value = sourceItbis ?? throw new SyncException("MISSING_FIELD", "DFC_ITBIS");
            RequireLineRoundingConfirmed(options);
            return value;
        }
        if (strategy != ItbisInterpretation.PercentageRate)
            throw new SyncException("FISCAL_RULE_UNCONFIRMED", "DFC_ITBIS");
        decimal percentage = sourceItbis ?? throw new SyncException("MISSING_FIELD", "DFC_ITBIS");
        decimal configured = PercentageForTaxCode(options, tax);
        if (percentage != configured)
            throw new SyncException("LINE_ITBIS_RATE_MISMATCH", "DFC_ITBIS");
        decimal baseAmount = Round2(checked(quantity * price) - discount);
        RequireLineRoundingConfirmed(options);
        return Round2(baseAmount * percentage / 100m);
    }

    private static void RequireLineRoundingConfirmed(FiscalMappingsOptions options)
    {
        if (!options.ItbisRoundedPerLineConfirmed)
            throw new SyncException("FISCAL_RULE_UNCONFIRMED", "ITBIS.redondeo_por_linea");
    }
}
