using System.Data.Common;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Security;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Repositories;

public sealed class FirebirdRepository(ConnectionFactory connections, IOptions<SyncOptions> sync,
    IOptions<FirebirdOptions> options, IOptions<WebPosOptions> webpos) : IFirebirdRepository
{
    private FbCommand Command(FbConnection c, FbTransaction t, string sql) => new(sql, c, t)
    { CommandTimeout = options.Value.CommandTimeoutSeconds };

    private static void Add(FbCommand c, string name, FbDbType type, object? value, int size = 0)
    {
        var p = c.Parameters.Add(name, type);
        if (size > 0) p.Size = size;
        p.Value = value ?? DBNull.Value;
    }

    private static void Key(FbCommand c, InvoiceKey key)
    {
        Add(c, "@company", FbDbType.Integer, key.IdCompania);
        Add(c, "@invoice", FbDbType.Integer, key.IdFactura);
    }

    private static FbTransactionOptions ReadTransaction() => new()
    {
        TransactionBehavior = FbTransactionBehavior.ReadCommitted | FbTransactionBehavior.RecVersion
            | FbTransactionBehavior.Read | FbTransactionBehavior.NoWait
    };

    private InvoiceCutover Cutover() => InvoiceCutover.From(webpos.Value, sync.Value);

    public async Task<IReadOnlyList<InvoiceKey>> GetCandidateInvoicesAsync(int fromIdInclusive, int? toIdExclusive, CancellationToken ct)
    {
        var cutover = Cutover();
        int from = Math.Max(fromIdInclusive, cutover.InitialId);
        if (toIdExclusive.HasValue && toIdExclusive.Value <= from) return [];
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(ReadTransaction(), ct);
        await using var cmd = Command(c, t, """
            SELECT FIRST (@batch) F.IDCOMPANIA,F.IDFACTURA
            FROM FACTURAS F
            WHERE F.IDCOMPANIA=@company AND F.IDFACTURA>=@from AND F.FAC_FECHA>=@start
              AND (@to IS NULL OR F.IDFACTURA < @to)
              AND (F.FAC_STATUS IS NULL OR F.FAC_STATUS<>'ANU')
              AND EXISTS (SELECT 1 FROM DET_FACTURA D
                           WHERE D.IDCOMPANIA=F.IDCOMPANIA AND D.IDFACTURA=F.IDFACTURA)
              AND EXISTS (SELECT 1 FROM FORMAS_PAGO P
                           WHERE P.IDCOMPANIA=F.IDCOMPANIA AND P.IDFACTURA=F.IDFACTURA
                             AND P.FPA_STATUS='EMI')
              AND NOT EXISTS (SELECT 1 FROM FORMAS_PAGO P
                               WHERE P.IDCOMPANIA=F.IDCOMPANIA AND P.IDFACTURA=F.IDFACTURA
                                 AND (P.FPA_STATUS IS NULL OR P.FPA_STATUS<>'EMI'))
            ORDER BY F.IDFACTURA
            """);
        Add(cmd, "@batch", FbDbType.Integer, sync.Value.BatchSize);
        Add(cmd, "@company", FbDbType.Integer, cutover.Company);
        Add(cmd, "@from", FbDbType.Integer, from);
        Add(cmd, "@to", FbDbType.Integer, toIdExclusive);
        Add(cmd, "@start", FbDbType.TimeStamp, cutover.Start);
        var result = new List<InvoiceKey>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) result.Add(new(r.GetInt32(0), r.GetInt32(1)));
        await t.CommitAsync(ct);
        return result;
    }

    public async Task<InvoiceSnapshot> ReadSnapshotAsync(InvoiceKey key, CancellationToken ct)
    {
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        // One SNAPSHOT transaction covers header, details and payments.
        await using var t = await c.BeginTransactionAsync(new FbTransactionOptions
        {
            TransactionBehavior = FbTransactionBehavior.Concurrency | FbTransactionBehavior.Read | FbTransactionBehavior.NoWait
        }, ct);
        SourceHeader? header = null;
        await using (var cmd = Command(c, t, """
            SELECT IDCOMPANIA,IDFACTURA,IDCLIENTE,FAC_FECHA,FAC_NOMBRE,FAC_DIRECCION1,FAC_DIRECCION2,
              FAC_ITBIS,FAC_TOTAL,FAC_PAGADO,FAC_DESCUENTO,FAC_STATUS,FAC_NCF,FAC_FECHAVENCE,
              FAC_TIPO,TIPONCF,FAC_DEVUELTO
            FROM FACTURAS WHERE IDCOMPANIA=@company AND IDFACTURA=@invoice
            """))
        {
            Key(cmd, key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
                header = new(Int(r,0),Int(r,1),NInt(r,2),Date(r,3),Text(r,4),Text(r,5),Text(r,6),
                    Money(r,7),Money(r,8),Money(r,9),Money(r,10),Text(r,11),Text(r,12),Date(r,13),Text(r,14),Text(r,15),Money(r,16));
            if (await r.ReadAsync(ct)) throw new SyncException("DUPLICATE_HEADER", "FACTURAS");
        }
        var details = new List<SourceDetail>();
        await using (var cmd = Command(c, t, """
            SELECT IDCOMPANIA,IDFACTURA,SECUENCIA,IDPRODUCTO,DFC_REFERENCIA,DFC_DESCRIPCION,
              DFC_CANTIDAD,DFC_PRECIO,DFC_DESCUENTO,DFC_ITBIS,DFC_STATUS,DFC_CANTDEVUELTA,ITBIS,MEDIDA
            FROM DET_FACTURA WHERE IDCOMPANIA=@company AND IDFACTURA=@invoice ORDER BY SECUENCIA
            """))
        {
            Key(cmd, key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (details.Count >= sync.Value.MaxInvoiceRows) throw new SyncException("ROW_LIMIT", "DET_FACTURA");
                details.Add(new(Int(r,0),Int(r,1),Int(r,2),NInt(r,3),Text(r,4),Text(r,5),Money(r,6),Money(r,7),
                    Money(r,8),Money(r,9),Text(r,10),Money(r,11),Text(r,12),Text(r,13)));
            }
        }
        var payments = new List<SourcePayment>();
        await using (var cmd = Command(c, t, """
            SELECT IDCOMPANIA,IDFACTURA,FPA_SECUENCIA,FPA_TIPO,FPA_MONTO,FPA_STATUS,FPA_EFECTIVO,
              FPA_TARJETAMONTO,FPA_TRANSFERENCIA,FPA_CHEQUEMONTO,FPA_CREDITO,FPA_BANCO
            FROM FORMAS_PAGO WHERE IDCOMPANIA=@company AND IDFACTURA=@invoice ORDER BY FPA_SECUENCIA
            """))
        {
            Key(cmd, key);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                if (payments.Count >= sync.Value.MaxInvoiceRows) throw new SyncException("ROW_LIMIT", "FORMAS_PAGO");
                payments.Add(new(Int(r,0),Int(r,1),Int(r,2),Text(r,3),Money(r,4),Text(r,5),Money(r,6),
                    Money(r,7),Money(r,8),Money(r,9),Money(r,10),Text(r,11)));
            }
        }
        await t.CommitAsync(ct);
        return new(key, header, details, payments);
    }

    private static int Int(DbDataReader r, int i) => NInt(r, i) ?? throw new SyncException("MISSING_FIELD", r.GetName(i));
    private static int? NInt(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToInt32(r.GetValue(i), CultureInfo.InvariantCulture);
    private static string? Text(DbDataReader r, int i) => r.IsDBNull(i) ? null : Convert.ToString(r.GetValue(i), CultureInfo.InvariantCulture)?.TrimEnd();
    private static DateTime? Date(DbDataReader r, int i) => r.IsDBNull(i) ? null : DateTime.SpecifyKind(r.GetDateTime(i), DateTimeKind.Unspecified);
    private static decimal? Money(DbDataReader r, int i)
    {
        if (r.IsDBNull(i)) return null;
        if (r.GetValue(i) is double or float) throw new SyncException("FLOAT_SOURCE_REQUIRES_CONFIRMED_DECIMAL_CONVERSION", r.GetName(i));
        return Convert.ToDecimal(r.GetValue(i), CultureInfo.InvariantCulture);
    }
}
