using FirebirdSql.Data.FirebirdClient;
using JomliaWebPosSync.Configuration;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace JomliaWebPosSync.Security;

public sealed class ConnectionFactory(IOptions<FirebirdOptions> firebird, IOptions<SqlServerOptions> sql)
{
    public FbConnection CreateFirebird() => CreateFirebird(firebird.Value);

    public SqlConnection CreateSqlServer() => CreateSqlServer(sql.Value);

    public static FbConnection CreateFirebird(FirebirdOptions o) => new(new FbConnectionStringBuilder
    {
        DataSource = o.Host, Port = o.Port, Database = o.Database, Charset = o.Charset,
        UserID = o.User, Password = o.Password, Role = o.Role, Dialect = 3,
        ConnectionTimeout = o.ConnectionTimeoutSeconds, Pooling = true
    }.ConnectionString);

    public static SqlConnection CreateSqlServer(SqlServerOptions o)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = o.Server, InitialCatalog = o.Database,
            IntegratedSecurity = o.IntegratedSecurity, TrustServerCertificate = o.TrustServerCertificate,
            Encrypt = o.Encrypt ? SqlConnectionEncryptOption.Mandatory : SqlConnectionEncryptOption.Optional,
            ConnectTimeout = o.ConnectionTimeoutSeconds, ConnectRetryCount = 0,
            ApplicationName = "JomliaWebPosSync", PersistSecurityInfo = false, Pooling = true
        };
        if (!o.IntegratedSecurity) { builder.UserID = o.User; builder.Password = o.Password; }
        return new SqlConnection(builder.ConnectionString);
    }
}
