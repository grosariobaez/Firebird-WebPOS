namespace JomliaWebPosSync.Configuration;

public sealed class SyncOptions
{
    public bool Enabled { get; set; }
    public DateTime? InvoiceStart { get; set; }
    public int PollIntervalSeconds { get; set; } = 3;
    public int BatchSize { get; set; } = 10;
    public int MaxInvoiceRows { get; set; } = 10000;
    public int MaxPayloadBytes { get; set; } = 16 * 1024 * 1024;
    public string PayloadDirectory { get; set; } = @"C:\ProgramData\PuntoAparte\JomliaWebPosSync\Outbox";
    public RetrySettings RetrySettings { get; set; } = new();
}

public sealed class RetrySettings
{
    public int InitialRetryDelaySeconds { get; set; } = 5;
    public int MaxRetryDelaySeconds { get; set; } = 300;
}

public sealed class FirebirdOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 3050;
    public string Database { get; set; } = "";
    public string Charset { get; set; } = "";
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public int CommandTimeoutSeconds { get; set; } = 15;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string Role { get; set; } = "";
}

public sealed class SqlServerOptions
{
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public bool Encrypt { get; set; } = true;
    public bool TrustServerCertificate { get; set; }
    public bool IntegratedSecurity { get; set; } = true;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public int ConnectionTimeoutSeconds { get; set; } = 15;
    public int CommandTimeoutSeconds { get; set; } = 30;
}

public sealed class WebPosOptions
{
    public string SourceSystem { get; set; } = "JOMLIA";
    public string CompanyLicCod { get; set; } = "";
    public string BranchCod { get; set; } = "";
    public string PosCod { get; set; } = "";
    // A single destination per service. Never mix Firebird companies implicitly.
    public int? SourceCompanyId { get; set; }
    public Dictionary<int, int> InitialInvoiceIdByCompany { get; set; } = new();
    public bool AllowMonetaryLineDiscount { get; set; }
}