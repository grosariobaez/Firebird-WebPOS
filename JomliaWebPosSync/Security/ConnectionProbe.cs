using JomliaWebPosSync.Configuration;

namespace JomliaWebPosSync.Security;

public sealed record ConnectionProbeResult(bool Ok, string Message, IReadOnlyList<string> Details);

public sealed class ConnectionProbe
{
    public async Task<ConnectionProbeResult> TestFirebirdAsync(FirebirdOptions options, CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        try
        {
            await using var connection = ConnectionFactory.CreateFirebird(options);
            await connection.OpenAsync(cancellationToken);
            await using (var probe = connection.CreateCommand())
            {
                probe.CommandTimeout = options.CommandTimeoutSeconds;
                probe.CommandText = "SELECT COUNT(*) FROM RDB$RELATIONS WHERE RDB$RELATION_NAME IN ('FACTURAS', 'DET_FACTURA', 'FORMAS_PAGO')";
                var count = Convert.ToInt32(await probe.ExecuteScalarAsync(cancellationToken));
                details.Add($"Tablas de origen encontradas: {count}/3");
                if (count != 3) details.Add("Faltan FACTURAS, DET_FACTURA o FORMAS_PAGO en la base indicada.");
            }
            foreach (var table in new[] { "FACTURAS", "DET_FACTURA", "FORMAS_PAGO" })
            {
                await using var command = connection.CreateCommand();
                command.CommandTimeout = options.CommandTimeoutSeconds;
                command.CommandText = $"SELECT 1 FROM {table} WHERE 1 = 0";
                await command.ExecuteScalarAsync(cancellationToken);
            }
            details.Add("SELECT de solo lectura verificado en las tres tablas.");
            var ok = details.Count == 2;
            return new ConnectionProbeResult(ok, ok ? "Firebird: conexión y permisos de lectura correctos." : "Firebird: la conexión abrió, pero faltan objetos o permisos.", details);
        }
        catch (Exception ex)
        {
            return new ConnectionProbeResult(false, "Firebird: la conexión o la verificación falló.", [Sanitize(ex.Message), .. details]);
        }
    }

    public async Task<ConnectionProbeResult> TestSqlServerAsync(SqlServerOptions options, CancellationToken cancellationToken = default)
    {
        var details = new List<string>();
        try
        {
            await using var connection = ConnectionFactory.CreateSqlServer(options);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = options.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT
                  DB_NAME(),
                  SUSER_SNAME(),
                  HAS_PERMS_BY_NAME('dbo.usp_ImportarFacturaFirebird', 'OBJECT', 'EXECUTE'),
                  HAS_PERMS_BY_NAME('dbo.PA_FB_SourceMap', 'OBJECT', 'SELECT'),
                  HAS_PERMS_BY_NAME('dbo.PA_FB_PaymentTypeMap', 'OBJECT', 'SELECT'),
                  OBJECT_ID(N'dbo.Header'),
                  OBJECT_ID(N'dbo.Detail'),
                  OBJECT_ID(N'dbo.Payment'),
                  OBJECT_ID(N'dbo.usp_ImportarFacturaFirebird'),
                  OBJECT_ID(N'dbo.PA_FB_SourceMap'),
                  OBJECT_ID(N'dbo.PA_FB_PaymentTypeMap')
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                return new ConnectionProbeResult(false, "SQL Server: la consulta de verificación no devolvió filas.", details);
            details.Add($"Base de datos: {reader.GetString(0)}");
            details.Add($"Login efectivo: {reader.GetString(1)}");
            var execute = ReadPermission(reader, 2, "EXECUTE en dbo.usp_ImportarFacturaFirebird", details);
            var sourceMap = ReadPermission(reader, 3, "SELECT en dbo.PA_FB_SourceMap", details);
            var paymentMap = ReadPermission(reader, 4, "SELECT en dbo.PA_FB_PaymentTypeMap", details);
            var objects = new[] { 5, 6, 7, 8, 9, 10 }
                .Select((index, order) => new { Name = new[] { "Header", "Detail", "Payment", "usp_ImportarFacturaFirebird", "PA_FB_SourceMap", "PA_FB_PaymentTypeMap" }[order], Present = !reader.IsDBNull(index) });
            foreach (var item in objects)
            {
                details.Add($"{item.Name}: {(item.Present ? "existe" : "NO existe")}");
                if (!item.Present) details.Add($"Falta el objeto {item.Name} en la base indicada.");
            }
            var ok = execute && sourceMap && paymentMap && objects.All(o => o.Present);
            return new ConnectionProbeResult(ok, ok ? "SQL Server: conexión, objetos y permisos correctos." : "SQL Server: la conexión abrió, pero faltan objetos o permisos.", details);
        }
        catch (Exception ex)
        {
            return new ConnectionProbeResult(false, "SQL Server: la conexión o la verificación falló.", [Sanitize(ex.Message), .. details]);
        }
    }

    private static bool ReadPermission(System.Data.Common.DbDataReader reader, int ordinal, string description, List<string> details)
    {
        var granted = !reader.IsDBNull(ordinal) && reader.GetInt32(ordinal) == 1;
        details.Add($"{description}: {(granted ? "concedido" : "NO concedido")}");
        if (!granted) details.Add($"Falta {description.ToLowerInvariant()}.");
        return granted;
    }

    private static string Sanitize(string message)
    {
        var text = message.ReplaceLineEndings(" ").Trim();
        return text.Length <= 300 ? text : text[..300] + "...";
    }
}
