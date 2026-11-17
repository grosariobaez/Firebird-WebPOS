using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Services;

internal static class PaymentConsistency
{
    private static readonly string[] ComponentNames = ["Efectivo", "TarjetaMonto", "Transferencia", "ChequeMonto", "Credito"];

    public static void Validate(InvoiceSnapshot snapshot, FiscalMappingsOptions options)
    {
        decimal paid = 0;
        foreach (var payment in snapshot.Payments)
        {
            string type = payment.Tipo ?? throw new SyncException("MISSING_FIELD", "FPA_TIPO");
            if (!options.PaymentConsistencyRules.TryGetValue(type, out var rule) || rule is null)
                throw new SyncException("PAYMENT_RULE_UNCONFIRMED", "FPA_TIPO");
            string expected = Validated(rule.ExpectedComponent);
            decimal amount = payment.Monto ?? throw new SyncException("MISSING_FIELD", "FPA_MONTO");
            decimal? primary = Value(expected, payment);
            if (primary is null || primary != amount)
            {
                if (amount != 0 && primary is (null or 0) && ComponentNames.Where(c => c != expected).Select(c => Value(c, payment)).Any(v => v == amount))
                    throw new SyncException("PAYMENT_COMPONENT_CONFLICT", "FPA_TIPO/FPA_COMPONENTES");
                throw new SyncException("PAYMENT_COMPONENT_MISMATCH", "FPA_" + expected);
            }
            foreach (string forbidden in rule.ZeroComponents ?? [])
            {
                string component = Validated(forbidden);
                if (component == expected)
                    throw new SyncException("PAYMENT_RULE_INVALID", "FPA_TIPO");
                decimal? value = Value(component, payment);
                if (value is not null && value != 0)
                    throw new SyncException("PAYMENT_COMPONENT_CONFLICT", "FPA_" + component);
            }
            if (rule.PaidIncludesCredit || expected != "Credito") paid = checked(paid + amount);
        }
        var header = snapshot.Header ?? throw new SyncException("MISSING_HEADER", "FACTURAS");
        decimal headerPaid = header.Pagado ?? throw new SyncException("MISSING_FIELD", "FAC_PAGADO");
        if (headerPaid != paid)
            throw new SyncException("PAID_COMPONENT_MISMATCH", "FAC_PAGADO");
    }

    private static string Validated(string? name) =>
        name is not null && ComponentNames.Contains(name, StringComparer.Ordinal) ? name
        : throw new SyncException("PAYMENT_RULE_INVALID", "FPA_TIPO");

    private static decimal? Value(string component, SourcePayment payment) => component switch
    {
        "Efectivo" => payment.Efectivo,
        "TarjetaMonto" => payment.TarjetaMonto,
        "Transferencia" => payment.Transferencia,
        "ChequeMonto" => payment.ChequeMonto,
        "Credito" => payment.Credito,
        _ => throw new SyncException("PAYMENT_RULE_INVALID", "FPA_TIPO")
    };
}
