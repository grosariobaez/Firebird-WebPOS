using System.Data.Common;
using System.Diagnostics;
using System.Globalization;
using FirebirdSql.Data.FirebirdClient;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Security;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Repositories;

public sealed class FirebirdRepository(ConnectionFactory connections, IOptions<SyncOptions> sync,
    IOptions<FirebirdOptions> options, IOptions<WebPosOptions> webpos, SyncDiagnostics diagnostics) : IFirebirdRepository
{
    private const string Owned = """
        IDCOMPANIA=@company AND IDFACTURA=@invoice AND ESTADO='R'
        AND ULTIMO_ERROR=@token AND PROXIMO_INTENTO>CURRENT_TIMESTAMP
        """;

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
    private static FbTransactionOptions QueueTransaction() => new()
    {
        TransactionBehavior = FbTransactionBehavior.ReadCommitted | FbTransactionBehavior.RecVersion
            | FbTransactionBehavior.Write | FbTransactionBehavior.NoWait
    };

    public async Task<IReadOnlyList<PendingInvoice>> GetPendingInvoicesAsync(CancellationToken ct)
    {
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(QueueTransaction(), ct);
        await using var cmd = Command(c, t, """
            SELECT FIRST (@batch) IDCOMPANIA,IDFACTURA,FECHA_EVENTO,INTENTOS
            FROM PA_FB_OUTBOX
            WHERE IDCOMPANIA=@company AND ESTADO='P'
              AND (PROXIMO_INTENTO IS NULL OR PROXIMO_INTENTO<=CURRENT_TIMESTAMP)
              AND FECHA_EVENTO<=DATEADD(SECOND, -@settle, CURRENT_TIMESTAMP)
            ORDER BY FECHA_EVENTO,IDCOMPANIA,IDFACTURA
            """);
        Add(cmd, "@batch", FbDbType.Integer, sync.Value.BatchSize);
        Add(cmd, "@company", FbDbType.Integer, webpos.Value.SourceCompanyId);
        Add(cmd, "@settle", FbDbType.Integer, sync.Value.SettleDelaySeconds);
        var result = new List<PendingInvoice>();
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct))
                result.Add(new(new(r.GetInt32(0), r.GetInt32(1)), r.GetDateTime(2), r.GetInt32(3)));
        await t.CommitAsync(ct);
        return result;
    }

    public async Task<InvoiceClaim?> TryClaimInvoiceAsync(PendingInvoice invoice, CancellationToken ct)
    {
        string token = "CLAIM:" + Guid.NewGuid().ToString("N");
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(QueueTransaction(), ct);
        await using var cmd = Command(c, t, """
            UPDATE PA_FB_OUTBOX SET ESTADO='R',
              INTENTOS=CASE WHEN INTENTOS<2147483647 THEN INTENTOS+1 ELSE INTENTOS END,
              PROXIMO_INTENTO=DATEADD(SECOND,@lease,CURRENT_TIMESTAMP), ULTIMO_ERROR=@token
            WHERE IDCOMPANIA=@company AND IDFACTURA=@invoice AND ESTADO='P'
              AND FECHA_EVENTO=@event AND INTENTOS=@attempts
              AND (PROXIMO_INTENTO IS NULL OR PROXIMO_INTENTO<=CURRENT_TIMESTAMP)
              AND FECHA_EVENTO<=DATEADD(SECOND,-@settle,CURRENT_TIMESTAMP)
            """);
        Key(cmd, invoice.Key);
        Add(cmd, "@event", FbDbType.TimeStamp, invoice.EventAt);
        Add(cmd, "@attempts", FbDbType.Integer, invoice.Attempts);
        Add(cmd, "@lease", FbDbType.Integer, sync.Value.ClaimLeaseSeconds);
        Add(cmd, "@settle", FbDbType.Integer, sync.Value.SettleDelaySeconds);
        Add(cmd, "@token", FbDbType.VarChar, token, 500);
        int affected = await cmd.ExecuteNonQueryAsync(ct);
        await t.CommitAsync(ct);
        return affected == 1 ? new(invoice.Key, token, invoice.EventAt,
            invoice.Attempts == int.MaxValue ? int.MaxValue : invoice.Attempts + 1) : null;
    }

    private async Task<bool> TransitionAsync(InvoiceClaim claim, string assignment,
        Action<FbCommand>? parameters, CancellationToken ct)
    {
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(QueueTransaction(), ct);
        // assignment is only a literal supplied by the methods below, never configuration/input.
        await using var cmd = Command(c, t, "UPDATE PA_FB_OUTBOX SET " + assignment + " WHERE " + Owned);
        Key(cmd, claim.Key);
        Add(cmd, "@token", FbDbType.VarChar, claim.Token, 500);
        parameters?.Invoke(cmd);
        int affected = await cmd.ExecuteNonQueryAsync(ct);
        await t.CommitAsync(ct);
        return affected == 1;
    }

    public Task<bool> RenewClaimAsync(InvoiceClaim claim, CancellationToken ct) =>
        TransitionAsync(claim, "PROXIMO_INTENTO=DATEADD(SECOND,@lease,CURRENT_TIMESTAMP)",
            c => Add(c, "@lease", FbDbType.Integer, sync.Value.ClaimLeaseSeconds), ct);
    public Task<bool> MarkAsSynchronizedAsync(InvoiceClaim claim, CancellationToken ct) =>
        TransitionAsync(claim, "ESTADO='S',FECHA_PROCESADO=CURRENT_TIMESTAMP,PROXIMO_INTENTO=NULL,ULTIMO_ERROR=NULL", null, ct);
    public Task<bool> MarkForRetryAsync(InvoiceClaim claim, int delaySeconds, string error, CancellationToken ct) =>
        TransitionAsync(claim, "ESTADO='P',PROXIMO_INTENTO=DATEADD(SECOND,@delay,CURRENT_TIMESTAMP),ULTIMO_ERROR=@error",
            c => { Add(c, "@delay", FbDbType.Integer, delaySeconds); Add(c, "@error", FbDbType.VarChar, error, 500); }, ct);
    public Task<bool> MarkAsErrorAsync(InvoiceClaim claim, string error, CancellationToken ct) =>
        TransitionAsync(claim, "ESTADO='E',PROXIMO_INTENTO=NULL,ULTIMO_ERROR=@error",
            c => Add(c, "@error", FbDbType.VarChar, error, 500), ct);

    public async Task<int> RecoverExpiredClaimsAsync(CancellationToken ct)
    {
        var keys = new List<(InvoiceKey Key, int Attempt)>();
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        await using (var t = await c.BeginTransactionAsync(QueueTransaction(), ct))
        {
            await using var cmd = Command(c, t, """
                SELECT FIRST (@batch) IDCOMPANIA,IDFACTURA,INTENTOS FROM PA_FB_OUTBOX
                WHERE IDCOMPANIA=@company AND ESTADO='R'
                  AND (PROXIMO_INTENTO IS NULL OR PROXIMO_INTENTO<=CURRENT_TIMESTAMP)
                ORDER BY PROXIMO_INTENTO,IDFACTURA
                """);
            Add(cmd, "@batch", FbDbType.Integer, sync.Value.BatchSize);
            Add(cmd, "@company", FbDbType.Integer, webpos.Value.SourceCompanyId);
            await using (var r = await cmd.ExecuteReaderAsync(ct))
                while (await r.ReadAsync(ct)) keys.Add((new(r.GetInt32(0), r.GetInt32(1)), r.GetInt32(2)));
            await t.CommitAsync(ct);
        }
        int recovered = 0;
        foreach (var item in keys)
        {
            var elapsed = Stopwatch.StartNew();
            await using var t = await c.BeginTransactionAsync(QueueTransaction(), ct);
            await using var cmd = Command(c, t, """
                UPDATE PA_FB_OUTBOX SET ESTADO='P',PROXIMO_INTENTO=CURRENT_TIMESTAMP,
                  ULTIMO_ERROR='CLAIM_EXPIRED'
                WHERE IDCOMPANIA=@company AND IDFACTURA=@invoice AND ESTADO='R'
                  AND (PROXIMO_INTENTO IS NULL OR PROXIMO_INTENTO<=CURRENT_TIMESTAMP)
                """);
            Key(cmd, item.Key);
            int affected = await cmd.ExecuteNonQueryAsync(ct);
            await t.CommitAsync(ct);
            recovered += affected;
            if (affected == 1) diagnostics.Invoice("ExpiredClaimRecovered", item.Key, null, item.Attempt, "P", elapsed.Elapsed, "CLAIM_EXPIRED");
        }
        return recovered;
    }

    public async Task<string?> GetStateAsync(InvoiceKey key, CancellationToken ct)
    {
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(QueueTransaction(), ct);
        await using var cmd = Command(c, t, "SELECT ESTADO FROM PA_FB_OUTBOX WHERE IDCOMPANIA=@company AND IDFACTURA=@invoice");
        Key(cmd, key);
        var result = await cmd.ExecuteScalarAsync(ct);
        await t.CommitAsync(ct);
        return result is null or DBNull ? null : Convert.ToString(result, CultureInfo.InvariantCulture);
    }

    public async Task<QueueCounts> GetCountsAsync(CancellationToken ct)
    {
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        await using var t = await c.BeginTransactionAsync(QueueTransaction(), ct);
        await using var cmd = Command(c, t,
            "SELECT ESTADO,COUNT(*) FROM PA_FB_OUTBOX WHERE IDCOMPANIA=@company GROUP BY ESTADO");
        Add(cmd, "@company", FbDbType.Integer, webpos.Value.SourceCompanyId);
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) counts[r.GetString(0)] = Convert.ToInt64(r.GetValue(1), CultureInfo.InvariantCulture);
        await t.CommitAsync(ct);
        return new(counts.GetValueOrDefault("P"), counts.GetValueOrDefault("R"), counts.GetValueOrDefault("S"), counts.GetValueOrDefault("E"));
    }

    public async Task<InvoiceSnapshot> ReadSnapshotAsync(InvoiceKey key, CancellationToken ct)
    {
        await using var c = connections.CreateFirebird();
        await c.OpenAsync(ct);
        // Concurrency is Firebird SNAPSHOT. No fallback to READ COMMITTED or dirty reads.
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
