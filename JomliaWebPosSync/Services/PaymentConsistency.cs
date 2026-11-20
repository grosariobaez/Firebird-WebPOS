using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;

namespace JomliaWebPosSync.Services;

internal static class PaymentConsistency
{
    private static readonly string[] ComponentNames = ["Efectivo", "TarjetaMonto", "Transferencia", "ChequeMonto", "Credito"];

    public static void Validate(InvoiceSnapshot snapshot)
    {
        decimal paid = 0;
        foreach (var payment in snapshot.Payments)
        {
            string type = payment.Tipo ?? throw new SyncException("MISSING_FIELD", "FPA_TIPO");
            decimal amount = payment.Monto ?? throw new SyncException("MISSING_FIELD", "FPA_MONTO");
            if (string.Equals(type, "F6", StringComparison.Ordinal))
            {
                ValidateCreditF6(payment, amount);
                continue; // FAC_PAGADO can remain 0 for credit
            }
            throw new SyncException("PAYMENT_RULE_UNCONFIRMED", "FPA_TIPO");
        }
        var header = snapshot.Header ?? throw new SyncException("MISSING_HEADER", "FACTURAS");
        decimal headerPaid = header.Pagado ?? throw new SyncException("MISSING_FIELD", "FAC_PAGADO");
        if (headerPaid != paid)
            throw new SyncException("PAID_COMPONENT_MISMATCH", "FAC_PAGADO");
    }

    private static void ValidateCreditF6(SourcePayment payment, decimal amount)
    {
        decimal? credit = Value("Credito", payment);
        if (credit is null || credit != amount)
            throw new SyncException("PAYMENT_COMPONENT_MISMATCH", "FPA_CREDITO");
        foreach (string component in ComponentNames.Where(c => c != "Credito"))
        {
            decimal? value = Value(component, payment);
            if (value is not null && value != 0)
                throw new SyncException("PAYMENT_COMPONENT_CONFLICT", "FPA_" + component);
        }
    }

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
