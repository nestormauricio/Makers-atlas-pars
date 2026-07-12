using System.Net.Http;
using AtlasPars.Api.Endpoints;
using AtlasPars.Domain.Abstractions;
using AtlasPars.Domain.Policies;
using AtlasPars.Infrastructure.Audit;
using AtlasPars.Infrastructure.Policies;
using AtlasPars.Infrastructure.Signing;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

// --- Modo --healthcheck (ver deploy/Dockerfile, HEALTHCHECK): la imagen "chiseled" no tiene
// curl/wget, así que el propio binario se reutiliza como cliente HTTP mínimo para el probe.
if (args.Contains("--healthcheck"))
{
    try
    {
        var url = Environment.GetEnvironmentVariable("ATLAS_HEALTHCHECK_URL") ?? "http://127.0.0.1:8080/health";
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        var response = await http.GetAsync(url);
        return response.IsSuccessStatusCode ? 0 : 1;
    }
    catch
    {
        return 1;
    }
}

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true;
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

string PoliciesDir(IServiceProvider sp) => sp.GetRequiredService<IConfiguration>()["Atlas:PoliciesDirectory"] ?? "policies";
string AuditDir(IServiceProvider sp) => sp.GetRequiredService<IConfiguration>()["Atlas:AuditDirectory"] ?? "audit-data";

var earlyKeyProviderKind = builder.Configuration["Atlas:KeyProvider"] ?? "EnvVar";
if (builder.Environment.IsProduction() &&
    earlyKeyProviderKind.Equals("EnvVar", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATLAS_SIGNING_KEY")))
{
    throw new InvalidOperationException(
        "Arranque abortado: ASPNETCORE_ENVIRONMENT=Production con Atlas:KeyProvider=EnvVar pero " +
        "sin ATLAS_SIGNING_KEY configurada. Nunca se permite el fallback de desarrollo en producción " +
        "(ver THREAT-MODEL.md T-04 y docs/RUNBOOK.md 'Rotación de llaves'). Configura Atlas:KeyProvider=" +
        "AzureKeyVault o define ATLAS_SIGNING_KEY explícitamente.");
}

var otelResource = ResourceBuilder.CreateDefault().AddService(
    serviceName: "atlas-pars-api",
    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(otelResource)
            .AddSource(AuthorizationService.ActivitySourceName)
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter();
    })
    .WithMetrics(metrics =>
    {
        metrics
            .SetResourceBuilder(otelResource)
            .AddMeter(AuthorizationService.MeterName)
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter();
    });

if (!string.IsNullOrWhiteSpace(builder.Configuration["Atlas:OtlpEndpoint"]))
{
    builder.Services.Configure<OpenTelemetry.Exporter.OtlpExporterOptions>(o =>
        o.Endpoint = new Uri(builder.Configuration["Atlas:OtlpEndpoint"]!));
}

builder.Services.AddSingleton<PolicyEvaluator>();
builder.Services.AddSingleton<IPolicyStore>(sp => new FileSystemPolicyStore(PoliciesDir(sp)));
builder.Services.AddSingleton<IAuditStore>(sp => new JsonlAuditStore(AuditDir(sp)));

if (earlyKeyProviderKind.Equals("AzureKeyVault", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IKeyProvider, AzureKeyVaultKeyProvider>();
}
else
{
    builder.Services.AddSingleton<IKeyProvider, EnvKeyProvider>();
}

builder.Services.AddSingleton<IDecisionSigner, HmacDecisionSigner>();
builder.Services.AddSingleton<AuthorizationService>();

builder.Services.AddHealthChecks()
    .AddCheck<PoliciesDirectoryHealthCheck>("policies_directory")
    .AddCheck<AuditStoreWritableHealthCheck>("audit_store_writable")
    .AddCheck<SigningServiceHealthCheck>("signing_service");

var app = builder.Build();

app.MapPost("/authorize", async (AuthorizeHttpRequest body, AuthorizationService svc, CancellationToken ct) =>
{
    try
    {
        var decision = await svc.AuthorizeAsync(body.ToDomain(), ct);
        return Results.Ok(decision.ToHttp());
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        app.Logger.LogError(ex, "Invariante de dominio violado al autorizar");
        return Results.Problem(statusCode: 500, title: "internal_error");
    }
})
.WithName("Authorize")
.Produces<AuthorizeHttpResponse>(200)
.Produces(400);

app.MapGet("/audit/{decisionId:guid}", async (Guid decisionId, IAuditStore store, CancellationToken ct) =>
{
    var record = await store.GetByDecisionIdAsync(decisionId, ct);
    return record is null ? Results.NotFound() : Results.Ok(record);
})
.WithName("GetAuditRecord");

app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { service = "atlas-pars", status = "ok" }));

app.Run();
return 0;

public partial class Program { }

public sealed class PoliciesDirectoryHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly IConfiguration _config;
    public PoliciesDirectoryHealthCheck(IConfiguration config) => _config = config;

    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var dir = _config["Atlas:PoliciesDirectory"] ?? "policies";
        var healthy = Directory.Exists(dir);
        return Task.FromResult(healthy
            ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy($"'{dir}' existe.")
            : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy($"'{dir}' no existe."));
    }
}

public sealed class AuditStoreWritableHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly IConfiguration _config;
    public AuditStoreWritableHealthCheck(IConfiguration config) => _config = config;

    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var dir = _config["Atlas:AuditDirectory"] ?? "audit-data";
        try
        {
            Directory.CreateDirectory(dir);
            var probe = Path.Combine(dir, ".health-probe");
            File.WriteAllText(probe, DateTimeOffset.UtcNow.ToString("o"));
            return Task.FromResult(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy());
        }
        catch (Exception ex)
        {
            return Task.FromResult(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(ex.Message));
        }
    }
}

public sealed class SigningServiceHealthCheck : Microsoft.Extensions.Diagnostics.HealthChecks.IHealthCheck
{
    private readonly IDecisionSigner _signer;
    public SigningServiceHealthCheck(IDecisionSigner signer) => _signer = signer;

    public Task<Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult> CheckHealthAsync(
        Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var probe = new AtlasPars.Domain.Models.AuthorizationDecision
            {
                DecisionId = Guid.NewGuid(),
                TenantId = "health-check",
                Effect = AtlasPars.Domain.Models.Effect.Deny,
                Reason = "health-probe",
                PolicyId = "n/a",
                PolicyVersion = 0,
                EvaluatedAtUtc = DateTimeOffset.UtcNow,
                RequestHash = "health-probe"
            };
            var (signature, keyId) = _signer.Sign(probe);
            var ok = _signer.Verify(probe, signature, keyId);
            return Task.FromResult(ok
                ? Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Healthy($"keyId={keyId}")
                : Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy("Verify() del probe falló."));
        }
        catch (Exception ex)
        {
            return Task.FromResult(Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckResult.Unhealthy(ex.Message));
        }
    }
}
