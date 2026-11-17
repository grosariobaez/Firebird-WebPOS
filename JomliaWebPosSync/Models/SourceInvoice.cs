namespace JomliaWebPosSync.Models;

public readonly record struct InvoiceKey(int IdCompania, int IdFactura);
public sealed record PendingInvoice(InvoiceKey Key, DateTime EventAt, int Attempts);
public sealed record InvoiceClaim(InvoiceKey Key, string Token, DateTime EventAt, int Attempt);
public sealed record QueueCounts(long Pending, long Processing, long Synchronized, long Error);
public sealed record SourceEvent(InvoiceKey Key, string Code, DateTime EventAt);

public sealed record SourceHeader(
    int IdCompania, int IdFactura, int? IdCliente, DateTime? Fecha, string? Nombre,
    string? Direccion1, string? Direccion2, decimal? Itbis, decimal? Total,
    decimal? Pagado, decimal? Descuento, string? Status, string? Ncf,
    DateTime? FechaVence, string? Tipo, string? TipoNcf, decimal? Devuelto);

public sealed record SourceDetail(
    int IdCompania, int IdFactura, int Secuencia, int? IdProducto, string? Referencia,
    string? Descripcion, decimal? Cantidad, decimal? Precio, decimal? Descuento,
    decimal? Itbis, string? Status, decimal? CantidadDevuelta, string? TaxCode, string? Medida);

public sealed record SourcePayment(
    int IdCompania, int IdFactura, int Secuencia, string? Tipo, decimal? Monto, string? Status,
    decimal? Efectivo, decimal? TarjetaMonto, decimal? Transferencia, decimal? ChequeMonto,
    decimal? Credito, string? Banco);

public sealed record InvoiceSnapshot(InvoiceKey Key, SourceHeader? Header,
    IReadOnlyList<SourceDetail> Details, IReadOnlyList<SourcePayment> Payments);
