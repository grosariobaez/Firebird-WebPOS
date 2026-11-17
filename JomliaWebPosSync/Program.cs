using JomliaWebPosSync;
using JomliaWebPosSync.Configuration;
using JomliaWebPosSync.Contracts;
using JomliaWebPosSync.Diagnostics;
using JomliaWebPosSync.Repositories;
using JomliaWebPosSync.Resilience;
using JomliaWebPosSync.Security;
using JomliaWebPosSync.Serialization;
using JomliaWebPosSync.Services;
using Microsoft.Extensions.Options;

var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args, ContentRootPath = AppContext.BaseDirectory
});
// Local file is optional, excluded from source control. Environment is applied last.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: false);
builder.Configuration.AddEnvironmentVariables(prefix: "JOMLIA_SYNC_");
builder.Services.AddWindowsService(o => o.ServiceName = "JomliaWebPosSync");
builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.TimestampFormat = "O"; o.UseUtcTimestamp = true; });
// Database providers may emit SQL/connection details: disable their internal logs.
builder.Logging.AddFilter("FirebirdSql", LogLevel.None);
builder.Logging.AddFilter("Microsoft.Data.SqlClient", LogLevel.None);
builder.Services.Configure<FirebirdOptions>(builder.Configuration.GetSection("Firebird"));
builder.Services.Configure<SqlServerOptions>(builder.Configuration.GetSection("SqlServer"));
builder.Services.Configure<WebPosOptions>(builder.Configuration.GetSection("WebPos"));
builder.Services.Configure<FiscalMappingsOptions>(builder.Configuration.GetSection("FiscalMappings"));
builder.Services.AddSingleton<IValidateOptions<SyncOptions>, ConfigurationValidator>();
builder.Services.AddOptions<SyncOptions>().Bind(builder.Configuration.GetSection("Sync")).ValidateOnStart();
builder.Services.Configure<HostOptions>(o =>
{
    o.ShutdownTimeout = TimeSpan.FromSeconds(45);
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.StopHost;
});
builder.Services.AddSingleton<ConnectionFactory>();
builder.Services.AddSingleton<PayloadDirectorySecurity>();
builder.Services.AddSingleton<IFirebirdRepository, FirebirdRepository>();
builder.Services.AddSingleton<ISqlServerRepository, SqlServerRepository>();
builder.Services.AddSingleton<IPayloadStore, PayloadStore>();
builder.Services.AddSingleton<InvoiceReader>();
builder.Services.AddSingleton<InvoiceMapper>();
builder.Services.AddSingleton<InvoiceValidator>();
builder.Services.AddSingleton<InvoiceJsonSerializer>();
builder.Services.AddSingleton<RetryPolicy>();
builder.Services.AddSingleton<SyncDiagnostics>();
builder.Services.AddSingleton<SyncCoordinator>();
builder.Services.AddHostedService<Worker>();
await builder.Build().RunAsync();
