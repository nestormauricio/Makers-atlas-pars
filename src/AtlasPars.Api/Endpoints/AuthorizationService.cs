using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtlasPars.Domain.Abstractions;
using AtlasPars.Domain.Models;
using AtlasPars.Domain.Policies;

namespace AtlasPars.Api.Endpoints;

/// <summary>
/// Orquesta el caso de uso principal: evaluar -> firmar -> auditar -> responder.
/// Vive en Api en vez de un proyecto "Application" separado: para el tamaño de este PoC
/// (un solo caso de uso real) una capa Application adicional sería indirección sin beneficio
/// (ver ADR-0001, "saber decir no a lo innecesario"). Si el servicio crece con más casos de uso,
/// este es el primer punto de extracción a un proyecto propio.
///
/// Trazabilidad distribuida: cada llamada abre un Activity ("Authorize") con sub-spans
/// ("policy.evaluate", "decision.sign", "audit.append"), y cada uno queda etiquetado con
/// TenantId/Effect/DecisionId. Un backend OTLP (Jaeger/Tempo/Azure Monitor) puede reconstruir
/// la cadena completa de una decisión, correlacionada por TraceId con los logs estructurados
/// (ver Program.cs, AddJsonConsole con IncludeScopes=true).
/// </summary>
public sealed class AuthorizationService
{
    public const string ActivitySourceName = "AtlasPars.Authorization";
    public const string MeterName = "AtlasPars.Authorization";

    private static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0");
    private static readonly System.Diagnostics.Metrics.Meter Meter = new(MeterName, "1.0");
    private static readonly System.Diagnostics.Metrics.Counter<long> DecisionsCounter =
        Meter.CreateCounter<long>("atlas_pars_decisions_total", description: "Decisiones emitidas por efecto");
    private static readonly System.Diagnostics.Metrics.Histogram<double> LatencyHistogram =
        Meter.CreateHistogram<double>("atlas_pars_authorize_latency_ms", unit: "ms");

    private readonly IPolicyStore _policyStore;
    private readonly PolicyEvaluator _evaluator;
    private readonly IDecisionSigner _signer;
    private readonly IAuditStore _auditStore;
    private readonly ILogger<AuthorizationService> _logger;

    public AuthorizationService(
        IPolicyStore policyStore,
        PolicyEvaluator evaluator,
        IDecisionSigner signer,
        IAuditStore auditStore,
        ILogger<AuthorizationService> logger)
    {
        _policyStore = policyStore;
        _evaluator = evaluator;
        _signer = signer;
        _auditStore = auditStore;
        _logger = logger;
    }

    public async Task<AuthorizationDecision> AuthorizeAsync(AuthorizationRequest request, CancellationToken ct = default)
    {
        using var activity = ActivitySource.StartActivity("Authorize", ActivityKind.Server);
        activity?.SetTag("atlas.tenant_id", request.TenantId);
        activity?.SetTag("atlas.action", request.Action);
        activity?.SetTag("atlas.resource_type", request.Resource.Type);

        var sw = Stopwatch.StartNew();

        if (string.IsNullOrWhiteSpace(request.TenantId))
            throw new ArgumentException("tenantId es obligatorio.", nameof(request));

        IReadOnlyList<PolicyDocument> policies;
        using (var evalActivity = ActivitySource.StartActivity("policy.evaluate"))
        {
            policies = await _policyStore.GetActivePoliciesAsync(request.TenantId, ct);

            // Multi-tenant hard-guard: nunca evaluamos políticas de un tenant distinto al solicitado,
            // incluso si por bug el store devolviera algo cruzado (defensa en profundidad).
            if (policies.Any(p => p.TenantId != request.TenantId))
                throw new InvalidOperationException("Aislamiento de tenant violado: el store devolvió políticas de otro tenant.");

            evalActivity?.SetTag("atlas.policies_loaded", policies.Count);
        }

        var result = _evaluator.Evaluate(request, policies);
        activity?.SetTag("atlas.effect", result.Effect.ToString());
        activity?.SetTag("atlas.policy_id", result.PolicyId);
        activity?.SetTag("atlas.policy_version", result.PolicyVersion);
        activity?.SetTag("atlas.reason", result.Reason);

        var decision = new AuthorizationDecision
        {
            DecisionId = Guid.NewGuid(),
            TenantId = request.TenantId,
            Effect = result.Effect,
            Reason = result.Reason,
            PolicyId = result.PolicyId,
            PolicyVersion = result.PolicyVersion,
            EvaluatedAtUtc = DateTimeOffset.UtcNow,
            RequestHash = HashRequest(request),
            Obligations = result.Obligations
        };
        activity?.SetTag("atlas.decision_id", decision.DecisionId.ToString());

        using (var signActivity = ActivitySource.StartActivity("decision.sign"))
        {
            var (signature, keyId) = _signer.Sign(decision);
            decision.Signature = signature;
            decision.KeyId = keyId;
            signActivity?.SetTag("atlas.key_id", keyId);
        }

        using (var auditActivity = ActivitySource.StartActivity("audit.append"))
        {
            await _auditStore.AppendAsync(new AuditRecord
            {
                DecisionId = decision.DecisionId,
                TenantId = decision.TenantId,
                Request = request,
                Decision = decision,
                RecordedAtUtc = DateTimeOffset.UtcNow
            }, ct);
        }

        sw.Stop();
        LatencyHistogram.Record(sw.Elapsed.TotalMilliseconds);
        DecisionsCounter.Add(1, new KeyValuePair<string, object?>("effect", decision.Effect.ToString()),
                                 new KeyValuePair<string, object?>("tenant", decision.TenantId));

        _logger.LogInformation(
            "Decision {DecisionId} tenant={TenantId} effect={Effect} reason={Reason} latencyMs={LatencyMs} traceId={TraceId}",
            decision.DecisionId, decision.TenantId, decision.Effect, decision.Reason, sw.Elapsed.TotalMilliseconds,
            activity?.TraceId.ToString());

        return decision;
    }

    /// <summary>Hash determinista de la solicitud (para poder correlacionar auditoría <-> request sin
    /// tener que re-serializar el objeto completo bit a bit en cada verificación).</summary>
    private static string HashRequest(AuthorizationRequest request)
    {
        var json = JsonSerializer.Serialize(new
        {
            request.TenantId,
            request.Actor.Id,
            request.Actor.Roles,
            ResourceType = request.Resource.Type,
            ResourceId = request.Resource.Id,
            request.Action,
            request.IdempotencyKey
        });
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(bytes);
    }
}
