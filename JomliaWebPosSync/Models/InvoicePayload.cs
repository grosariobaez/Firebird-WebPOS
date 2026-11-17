namespace JomliaWebPosSync.Models;

// Serializer sorts properties explicitly, independently of reflection/declaration order.
public sealed record InvoicePayload(int IdCompania, int IdFactura, PayloadHeader Header,
    IReadOnlyList<PayloadDetail> Details, IReadOnlyList<PayloadPayment> Payments);
public sealed record PayloadHeader(string DocType, string Ncf, DateTime Fecha, DateOnly? NcfVencimiento,
    string? Nombre, string? Ruc, string? Direccion, string CustomerType, int TaxMode,
    string OperMode, string Currency, decimal CurrencyRate, int? PaymentType, int Tip,
    decimal Total, decimal Itbis, decimal Devuelto);
public sealed record PayloadDetail(int Secuencia, int IdProducto, string? Referencia,
    string Descripcion, decimal Cantidad, decimal Precio, decimal Descuento, decimal Itbis,
    decimal CantidadDevuelta, int Tax, int ItemClass, string Um);
public sealed record PayloadPayment(int Secuencia, string Tipo, decimal Monto, string Estatus, string? Descripcion);

public sealed record ImportRoute(string SourceSystem, string Server, string Database,
    string CompanyLicCod, string BranchCod, string PosCod, bool AllowMonetaryLineDiscount);
public sealed record PaymentExpectation(string SourceType, string TargetType);
public sealed record StoredPayload(int Version, InvoiceKey Key, ImportRoute Route, string Ncf,
    string SourceFingerprint, string Payload, string PayloadSha256,
    IReadOnlyList<PaymentExpectation> PaymentMappings);
public sealed record PayloadEnvelope(string Content, string Sha256);
