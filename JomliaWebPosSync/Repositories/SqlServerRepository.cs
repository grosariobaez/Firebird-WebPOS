using System.Data;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Models;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Security;
using JomliaWebPosSync.Serialization;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Repositories;

public sealed class SqlServerRepository(ConnectionFactory connections, IOptions<SqlServerOptions> options,
    IOptions<WebPosOptions> webpos) : ISqlServerRepository
{
    public ImportRoute CurrentRoute() => new(webpos.Value.SourceSystem, options.Value.Server, options.Value.Database,
        webpos.Value.CompanyLicCod, webpos.Value.BranchCod, webpos.Value.PosCod, webpos.Value.AllowMonetaryLineDiscount);

    public async Task<bool> IsImportedAsync(InvoiceKey key, CancellationToken ct)
    {
        await using var c = connections.CreateSqlServer();
        await c.OpenAsync(ct);
        await using var cmd = new SqlCommand("""
            SELECT 1 FROM dbo.PA_FB_SourceMap
            WHERE sourceSystem=@source AND idCompania=@company AND idFactura=@invoice AND docType='F'
            """, c) { CommandTimeout = options.Value.CommandTimeoutSeconds };
        cmd.Parameters.Add("@source", SqlDbType.NVarChar, 40).Value = webpos.Value.SourceSystem;
        cmd.Parameters.Add("@company", SqlDbType.Int).Value = key.IdCompania;
        cmd.Parameters.Add("@invoice", SqlDbType.Int).Value = key.IdFactura;
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<string> ImportAsync(StoredPayload payload, CancellationToken ct)
    {
        if (payload.Route != CurrentRoute()) throw new SyncException("PERSISTED_ROUTE_CHANGED", "WebPos/SqlServer");
        await using var c = connections.CreateSqlServer();
        await c.OpenAsync(ct);
        // V3's replay branch checks hash/NCF, but not all persisted destination columns.
        // Read the authoritative SourceMap to close that gap without changing its contract.
        await using (var identity = new SqlCommand("""
            SELECT companyLicCod,branchCod,posCod,docType,docNumber,payloadHash
            FROM dbo.PA_FB_SourceMap
            WHERE sourceSystem=@source AND idCompania=@company AND idFactura=@invoice
            """, c) { CommandTimeout = options.Value.CommandTimeoutSeconds })
        {
            identity.Parameters.Add("@source", SqlDbType.NVarChar, 40).Value = payload.Route.SourceSystem;
            identity.Parameters.Add("@company", SqlDbType.Int).Value = payload.Key.IdCompania;
            identity.Parameters.Add("@invoice", SqlDbType.Int).Value = payload.Key.IdFactura;
            await using var r = await identity.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                if (r.GetString(0) != payload.Route.CompanyLicCod || r.GetString(1) != payload.Route.BranchCod
                    || r.GetString(2) != payload.Route.PosCod || r.GetString(3) != "F" || r.GetString(4) != payload.Ncf)
                    throw new SyncException("DESTINATION_IDENTITY_CONFLICT", "PA_FB_SourceMap");
                if (Convert.ToHexString((byte[])r.GetValue(5)) != InvoiceJsonSerializer.HashSqlPayload(payload.Payload))
                    throw new SyncException("DESTINATION_PAYLOAD_CONFLICT", "PA_FB_SourceMap.payloadHash");
            }
        }
        // Validate the actual mapping used by the SP, not just the local dictionary.
        // These mappings must be maintained as controlled, immutable fiscal configuration during operation.
        foreach (var mapping in payload.PaymentMappings)
        {
            await using var check = new SqlCommand("SELECT webposTipo FROM dbo.PA_FB_PaymentTypeMap WHERE fpaTipo=@type", c)
            { CommandTimeout = options.Value.CommandTimeoutSeconds };
            check.Parameters.Add("@type", SqlDbType.NVarChar, 3).Value = mapping.SourceType;
            var actual = await check.ExecuteScalarAsync(ct);
            if (actual is not string code || !string.Equals(code, mapping.TargetType, StringComparison.Ordinal))
                throw new SyncException("DESTINATION_PAYMENT_MAPPING_MISMATCH", "PA_FB_PaymentTypeMap");
        }
        await using var cmd = new SqlCommand("dbo.usp_ImportarFacturaFirebird", c)
        { CommandType = CommandType.StoredProcedure, CommandTimeout = options.Value.CommandTimeoutSeconds };
        cmd.Parameters.Add("@SourceSystem", SqlDbType.NVarChar, 40).Value = payload.Route.SourceSystem;
        cmd.Parameters.Add("@CompanyLicCod", SqlDbType.NVarChar, 10).Value = payload.Route.CompanyLicCod;
        cmd.Parameters.Add("@BranchCod", SqlDbType.NVarChar, 4).Value = payload.Route.BranchCod;
        cmd.Parameters.Add("@PosCod", SqlDbType.NVarChar, 4).Value = payload.Route.PosCod;
        cmd.Parameters.Add("@Payload", SqlDbType.NVarChar, -1).Value = payload.Payload;
        cmd.Parameters.Add("@AllowMonetaryLineDiscount", SqlDbType.Bit).Value = payload.Route.AllowMonetaryLineDiscount;
        var output = cmd.Parameters.Add("@DocNumber", SqlDbType.NVarChar, 50);
        output.Direction = ParameterDirection.Output;
        // The procedure owns its transaction and Payment -> Detail -> Header order.
        await cmd.ExecuteNonQueryAsync(ct);
        if (output.Value is not string docNumber || !string.Equals(docNumber, payload.Ncf, StringComparison.Ordinal))
            throw new SyncException("SQL_ACK_DOCUMENT_MISMATCH", "DocNumber");
        return docNumber;
    }
}
