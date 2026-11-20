using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Services;

internal static class FiscalItbis
{
    public static decimal Round2(decimal value) => decimal.Round(value, 2, MidpointRounding.AwayFromZero);

    public static int ResolveTax(string? sourceTaxCode)
    {
        // Regla verificada IXU 1.0.4: DFC_ITBIS (clasificación ITBIS) NULL/vacío/espacios -> tax 1 (18%).
        if (string.IsNullOrWhiteSpace(sourceTaxCode))
            return 1;
        string code = sourceTaxCode.Trim();
        if (int.TryParse(code, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out int tax) && tax is >= 0 and <= 4)
            return tax;
        if (decimal.TryParse(code, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out decimal rate))
        {
            if (rate == 18m) return 1;
            if (rate == 16m) return 2;
            if (rate == 0m) return 0;
        }
        // No convertir códigos desconocidos no vacíos silenciosamente a 18%.
        throw new SyncException("UNKNOWN_FISCAL_CODE", "ITBIS.tax");
    }

    public static decimal LineAmount(int tax, decimal quantity, decimal price, decimal discount) =>
        Round2(Round2(checked(quantity * price) - discount) * JomliaWebPosSync.Configuration.FiscalMappingsOptions.RequiredItbisRate(tax) / 100m);
}
