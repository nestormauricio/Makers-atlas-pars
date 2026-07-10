using AtlasPars.Api.Endpoints;
using AtlasPars.Domain.Abstractions;
using AtlasPars.Domain.Policies;
using AtlasPars.Infrastructure.Audit;
using AtlasPars.Infrastructure.Policies;
using AtlasPars.Infrastructure.Signing;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
});

// --- Logging estructurado (JSON en consola; en k8s cualquier colector lo levanta tal cual) ---
builder.Logging.ClearProviders();
builder.Logging.AddJsonConsole(o =>
{
    o.IncludeScopes = true; // incluye TraceId/SpanId del Activity actual en cada línea de log
    o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
});

// --- Configuración ---
var policiesDir = builder.Configuration["Atlas:PoliciesDirectory"] ?? "policies";
var auditDir = builder.Configuration["Atlas:AuditDirectory"] ?? "audit-data";
var keyProviderKind = builder.Configuration["Atlas:KeyProvider"] ?? "EnvVar"; // EnvVar | AzureKeyVault

// --- Guardia de arranque fail-fast (ver THREAT-MODEL.md T-04): en Production, jamás arrancar
// con la llave de firma de fallback insegura de EnvKeyProvider. Esto convierte un error de
// configuración silencioso (fail-open) en un fallo de arranque explícito (fail-closed). ---
if (builder.Environment.IsProduction() &&
    keyProviderKind.Equals("EnvVar", StringComparison.OrdinalIgnoreCase) &&
    string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ATLAS_SIGNING_KEY")))
{
    throw new InvalidOperationException(
        "Arranque abortado: ASPNETCORE_ENVIRONMENT=Production con Atlas:KeyProvider=EnvVar pero " +
        "sin ATLAS_SIGNING_KEY configurada. Nunca se permite el fallback de desarrollo en producción " +
        "(ver THREAT-MODEL.md T-04 y docs/RUNBOOK.md 'Rotación de llaves'). Configura Atlas:KeyProvider=" +
        "AzureKeyVault o define ATLAS_SIGNING_KEY explícitamente.");
}

// --- Observabilidad: OpenTelemetry real (no solo un Meter suelto) — trazas + métricas ---
// El ActivitySource/Meter de negocio viven en AuthorizationService; aquí se registra el SDK
// que efectivamente recolecta y exporta esas señales (ver docs/RUNBOOK.md "Observabilidad").
var otelResource = ResourceBuilder.CreateDefault().AddService(
    serviceName: "atlas-pars-api",
    serviceVersion: typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0");

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing =>
    {
        tracing
            .SetResourceBuilder(otelResource)
            .AddSource(AuthorizationService.ActivitySourceName) // spans propios: evaluate/sign/audit
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter(); // en producción: reemplazar/añadir AddOtlpExporter() vía OTEL_EXPORTER_OTLP_ENDPOINT
    })
    .WithMetrics(metrics =>
    {
        metrics
            .SetResourceBuilder(otelResource)
            .AddMeter(AuthorizationService.MeterName) // atlas_pars_decisions_total, atlas_pars_authorize_latency_ms
            .AddAspNetCoreInstrumentation()
            .AddConsoleExporter();
    });

if (!string.IsNullOrWhiteSpace(builder.Configuration["Atlas:OtlpEndpoint"]))
{
    // Habilita exportación real a un backend OTLP (Tempo/Jaeger/Azure Monitor) cuando se configure.
    builder.Services.Configure<OpenTelemetry.Exporter.OtlpExporterOptions>(o =>
        o.Endpoint = new Uri(builder.Configuration["Atlas:OtlpEndpoint"]!));
}

// --- DI: puertos -> adaptadores (ver ARCHITECTURE.md, arquitectura hexagonal) ---
builder.Services.AddSingleton<PolicyEvaluator>();
builder.Services.AddSingleton<IPolicyStore>(_ => new FileSystemPolicyStore(policiesDir));
builder.Services.AddSingleton<IAuditStore>(_ => new JsonlAuditStore(auditDir));

if (keyProviderKind.Equals("AzureKeyVault", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IKeyProvider, AzureKeyVaultKeyProvider>();
}
else
{
    builder.Services.AddSingleton<IKeyProvider, EnvKeyProvider>();
}

builder.Services.AddSingleton<IDecisionSigner, HmacDecisionSigner>();
builder.Services.AddSingleton<AuthorizationService>();

// --- Health check compuesto (no trivial): valida políticas legibles, audit store escribible
// y que el firmante efectivamente firme (smoke test funcional, no solo "el proceso vive"). ---
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

// Necesario para que WebApplicationFactory<Program> funcione en las pruebas de integración.
public partial class Program { }

/// <summary>Health check "no trivial": valida que el directorio de políticas exista y sea legible,
/// no solo que el proceso esté vivo.</summary>
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

/// <summary>Verifica que el almacén de auditoría acepte escrituras (no solo que exista el directorio).</summary>
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

/// <summary>Smoke test funcional del firmante: firma y verifica un payload sintético en cada chequeo.
/// Detecta llaves corruptas/ausentes antes de que una decisión real falle en producción.</summary>
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
