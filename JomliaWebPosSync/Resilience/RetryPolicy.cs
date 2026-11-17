using System.Net.Sockets;
using FirebirdSql.Data.FirebirdClient;
using JomliaWebPosSync.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Resilience;

public sealed record Failure(bool Transient, string Code, string Field)
{
    public string SafeDescription => Code + ":" + Field;
}

public sealed class RetryPolicy(IOptions<SyncOptions> options)
{
    public int DelaySeconds(int attempt)
    {
        var o = options.Value.RetrySettings;
        double ceiling = Math.Min(o.MaxRetryDelaySeconds, o.InitialRetryDelaySeconds * Math.Pow(2, Math.Clamp(attempt - 1, 0, 20)));
        return Math.Max(1, (int)Math.Ceiling(ceiling * (0.5 + Random.Shared.NextDouble() * 0.5)));
    }

    public Failure Classify(Exception error)
    {
        if (error is SyncException known) return new(known.Transient, known.Code, known.Field);
        if (error is SqlException sql)
        {
            int[] transient = [-2, 0, 20, 53, 64, 121, 233, 258, 1205, 1222, 10053, 10054, 10060, 10928, 10929, 40197, 40501, 40613, 49918, 49919, 49920, 51132, 51133];
            // An explicit rejection takes precedence over ancillary errors such as 3621.
            foreach (SqlError item in sql.Errors)
                if ((item.Number is >= 51101 and <= 51136 && item.Number is not 51132 and not 51133)
                    || item.Number is 208 or 229 or 245 or 515 or 1105 or 2601 or 2627 or 2628 or 2812
                    or 4060 or 8114 or 8115 or 8152 or 9002 or 18456)
                    return new(false, "SQL_" + item.Number, "import");
            if (sql.Errors.Cast<SqlError>().Any(e => transient.Contains(e.Number))) return new(true, "SQL_TRANSIENT", "connection/command");
            // Unknown infrastructure errors remain recoverable; never exhaust a retry budget.
            return new(true, "SQL_UNCLASSIFIED_" + sql.Number, "connection/command");
        }
        if (error is FbException fb)
        {
            // ISC codes: metadata, missing object/column, credentials and permissions are actionable.
            int[] configuration = [335544352, 335544472, 335544551, 335544578, 335544580];
            if (fb.Errors.Cast<FbError>().Any(e => configuration.Contains(e.Number)))
                return new(false, "FIREBIRD_CONFIGURATION", "schema/permissions");
            return new(true, "FIREBIRD_TRANSIENT_OR_UNKNOWN", "connection/transaction");
        }
        if (error is IOException io)
        {
            int win32 = io.HResult & 0xFFFF;
            return win32 switch
            {
                0x05 => new(false, "ACCESS_DENIED", "PayloadDirectory"),
                0x20 or 0x21 => new(false, "FILE_LOCKED", "PayloadFile"),
                0x70 => new(false, "STORAGE_FULL", "PayloadDirectory"),
                _ when io is PathTooLongException or DirectoryNotFoundException or DriveNotFoundException or FileNotFoundException
                    => new(false, "STORAGE_PATH", "PayloadDirectory"),
                _ => new(false, "STORAGE_UNCLASSIFIED", "PayloadDirectory")
            };
        }
        if (error is SocketException or TimeoutException or OperationCanceledException)
            return new(true, "NETWORK_TIMEOUT_OR_CLAIM_LOST", "infrastructure");
        if (error is UnauthorizedAccessException) return new(false, "ACCESS_DENIED", "PayloadDirectory");
        if (error is System.Security.SecurityException) return new(false, "ACCESS_DENIED", "PayloadDirectory");
        if (error is OverflowException) return new(false, "DECIMAL_OVERFLOW", "amounts");
        return new(false, "UNEXPECTED_" + error.GetType().Name, "implementation/configuration");
    }
}
