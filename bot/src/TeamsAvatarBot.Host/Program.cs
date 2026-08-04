using System.Net;
using DotNetEnv;
using Microsoft.Extensions.Logging;
using Serilog;
using TeamsAvatarBot.Core;
using TeamsAvatarBot.LiveKitBridge;

// .env lives at the repo root, walk up from the Host bin/ to find it.
var repoRoot = LoadEnvFromRepoRoot();

var config = BotConfig.FromEnvironment();

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj} {Properties:j}{NewLine}{Exception}")
    .CreateLogger();

// Install the TLS cert into LocalMachine\My (idempotent) and capture the
// thumbprint so MediaPlatformSettings can find the same cert in the store.
// Skype.Bots.Media looks the cert up by thumbprint, not file path.
var certPath = Path.Combine(repoRoot, "certs", "iris.pfx");
var certPwd  = Environment.GetEnvironmentVariable("CERT_PFX_PASSWORD") ?? "changeit";

string? certThumbprint = null;
if (File.Exists(certPath) && config.IsAzureConfigured)
{
    using var bootstrapLogFactory = LoggerFactory.Create(b => b.AddSerilog());
    var certLogger = bootstrapLogFactory.CreateLogger("CertificateInstaller");
    try
    {
        certThumbprint = CertificateInstaller.EnsureInstalled(certPath, certPwd, certLogger);
    }
    catch (Exception ex)
    {
        Log.Error(ex, "cert installation failed; CommunicationsService will not register");
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseSerilog();
builder.Services.AddControllers();
builder.Services.AddSingleton(config);

// Bind Kestrel to the configured calling port using the Let's Encrypt cert.
// Skipped under WebApplicationFactory<Program> (tests use TestServer which
// ignores Kestrel options). Bind 443 requires elevated process on Windows.
builder.WebHost.ConfigureKestrel(options =>
{
    if (File.Exists(certPath))
    {
        options.Listen(IPAddress.Any, config.CallingPort, listen =>
        {
            listen.UseHttps(certPath, certPwd);
        });
        Log.Information("Kestrel HTTPS bound on :{Port} using {Cert}", config.CallingPort, certPath);
    }
    else
    {
        options.Listen(IPAddress.Any, 5000);
        Log.Warning(
            "Cert not found at {CertPath}; falling back to plain HTTP :5000. "
            + "Run scripts/install-tls-cert.ps1 to issue the cert.",
            certPath);
    }
});

// CommunicationsService needs Azure config + an installed cert thumbprint
// (Skype.Bots.Media wants to look the cert up in the local cert store).
if (config.IsAzureConfigured && certThumbprint is not null)
{
    builder.Services.AddSingleton<CommunicationsService>(sp => new CommunicationsService(
        config,
        certThumbprint,
        sp.GetRequiredService<ILogger<CommunicationsService>>(),
        sp.GetRequiredService<ILogger<CallHandler>>(),
        sp.GetRequiredService<ILoggerFactory>()));
    Log.Information("registering CommunicationsService (Azure config + cert present)");
}
else
{
    Log.Warning(
        "CommunicationsService NOT registered (azureConfigured={Az}, certThumbprint={Cert}). "
        + "/api/calling will return 503 until both are in place.",
        config.IsAzureConfigured, certThumbprint is not null);
}

var app = builder.Build();
app.UseSerilogRequestLogging();
app.MapControllers();

if (config.IsAzureConfigured && certThumbprint is not null)
{
    _ = app.Services.GetRequiredService<CommunicationsService>();
    Log.Information("CommunicationsService initialised");
}

Log.Information("teams-avatar-bot starting fqdn={Fqdn} port={Port} azureConfigured={Az}",
    config.PublicFqdn, config.CallingPort, config.IsAzureConfigured);

app.Run();

static string LoadEnvFromRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        var env = Path.Combine(dir.FullName, ".env");
        if (File.Exists(env))
        {
            Env.Load(env);
            return dir.FullName;
        }
        dir = dir.Parent;
    }
    throw new FileNotFoundException(
        ".env not found walking up from " + AppContext.BaseDirectory);
}

// Exposes the top-level Program class to WebApplicationFactory<Program> in tests.
public partial class Program;
