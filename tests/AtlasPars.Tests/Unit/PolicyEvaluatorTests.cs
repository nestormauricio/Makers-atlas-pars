using AtlasPars.Domain.Models;
using AtlasPars.Domain.Policies;
using Xunit;

namespace AtlasPars.Tests.Unit;

public class PolicyEvaluatorTests
{
    private readonly PolicyEvaluator _sut = new();

    private static AuthorizationRequest Request(
        string tenantId = "t1",
        string actorId = "u1",
        List<string>? roles = null,
        string resourceType = "account",
        string action = "transfer.create",
        Dictionary<string, object?>? context = null) => new()
    {
        TenantId = tenantId,
        Actor = new Actor { Id = actorId, Roles = roles ?? new List<string>() },
        Resource = new Resource { Type = resourceType, Id = "r1" },
        Action = action,
        Context = context ?? new Dictionary<string, object?>()
    };

    private static PolicyDocument Doc(string tenantId, IReadOnlyList<PolicyRule> rules, Effect defaultEffect = Effect.Deny)
        => new() { PolicyId = "p1", Version = 1, TenantId = tenantId, Description = "test", Rules = rules, DefaultEffect = defaultEffect };

    [Fact]
    public void Sin_politicas_devuelve_Deny_fail_closed()
    {
        var result = _sut.Evaluate(Request(), Array.Empty<PolicyDocument>());
        Assert.Equal(Effect.Deny, result.Effect);
        Assert.Equal("no-policy-defined", result.Reason);
    }

    [Fact]
    public void Sin_reglas_aplicables_usa_DefaultEffect_del_documento()
    {
        var doc = Doc("t1", Array.Empty<PolicyRule>(), defaultEffect: Effect.Deny);
        var result = _sut.Evaluate(Request(), new[] { doc });
        Assert.Equal(Effect.Deny, result.Effect);
        Assert.Equal("default-effect", result.Reason);
    }

    [Fact]
    public void Deny_gana_sobre_Permit_en_la_misma_prioridad_deny_overrides()
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "permit-all", Priority = 10, Effect = Effect.Permit,
                    Actions = new[] { "*" }, ResourceTypes = new[] { "*" } },
            new() { RuleId = "deny-sanctioned", Priority = 10, Effect = Effect.Deny,
                    Actions = new[] { "*" }, ResourceTypes = new[] { "*" },
                    Conditions = new[] { new PolicyCondition { Source = ConditionSource.Context, Attribute = "country", Operator = ConditionOperator.Equals, Value = "KP" } } }
        };
        var doc = Doc("t1", rules);
        var request = Request(context: new Dictionary<string, object?> { ["country"] = "KP" });

        var result = _sut.Evaluate(request, new[] { doc });

        Assert.Equal(Effect.Deny, result.Effect);
        Assert.Equal("rule:deny-sanctioned", result.Reason);
    }

    [Fact]
    public void Challenge_gana_sobre_Permit_pero_pierde_contra_Deny()
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "permit-role", Priority = 10, Effect = Effect.Permit,
                    Actions = new[] { "transfer.create" }, ResourceTypes = new[] { "account" },
                    Conditions = new[] { new PolicyCondition { Source = ConditionSource.Actor, Attribute = "roles", Operator = ConditionOperator.Contains, Value = "operator" } } },
            new() { RuleId = "challenge-amount", Priority = 20, Effect = Effect.Challenge,
                    Actions = new[] { "transfer.create" }, ResourceTypes = new[] { "account" },
                    Conditions = new[] { new PolicyCondition { Source = ConditionSource.Context, Attribute = "amount", Operator = ConditionOperator.GreaterThan, Value = 1000 } } }
        };
        var doc = Doc("t1", rules);
        var request = Request(roles: new List<string> { "operator" }, context: new Dictionary<string, object?> { ["amount"] = 5000 });

        var result = _sut.Evaluate(request, new[] { doc });

        Assert.Equal(Effect.Challenge, result.Effect);
    }

    [Fact]
    public void Permit_por_rol_cuando_no_hay_reglas_de_mayor_severidad()
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "permit-role", Priority = 10, Effect = Effect.Permit,
                    Actions = new[] { "transfer.create" }, ResourceTypes = new[] { "account" },
                    Conditions = new[] { new PolicyCondition { Source = ConditionSource.Actor, Attribute = "roles", Operator = ConditionOperator.Contains, Value = "operator" } } }
        };
        var doc = Doc("t1", rules);
        var request = Request(roles: new List<string> { "operator" }, context: new Dictionary<string, object?> { ["amount"] = 10 });

        var result = _sut.Evaluate(request, new[] { doc });

        Assert.Equal(Effect.Permit, result.Effect);
    }

    [Fact]
    public void Operador_In_evalua_correctamente_listas_separadas_por_coma()
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "deny-country", Priority = 10, Effect = Effect.Deny,
                    Actions = new[] { "*" }, ResourceTypes = new[] { "*" },
                    Conditions = new[] { new PolicyCondition { Source = ConditionSource.Context, Attribute = "country", Operator = ConditionOperator.In, Value = "IR,KP,SY" } } }
        };
        var doc = Doc("t1", rules);

        var deny = _sut.Evaluate(Request(context: new Dictionary<string, object?> { ["country"] = "KP" }), new[] { doc });
        var notDeny = _sut.Evaluate(Request(context: new Dictionary<string, object?> { ["country"] = "CO" }), new[] { doc });

        Assert.Equal(Effect.Deny, deny.Effect);
        Assert.Equal(Effect.Deny, notDeny.Effect); // cae al default-effect (Deny), no hay permit
        Assert.Equal("rule:deny-country", deny.Reason);
        Assert.Equal("default-effect", notDeny.Reason);
    }

    [Fact]
    public void Politicas_de_otro_tenant_no_se_evaluan_aislamiento_multi_tenant()
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "permit-all", Priority = 10, Effect = Effect.Permit,
                    Actions = new[] { "*" }, ResourceTypes = new[] { "*" } }
        };
        var docTenantA = Doc("tenant-a", rules);

        // El propio evaluador es puro: si el caller le pasa por error políticas de otro tenant,
        // las evalúa (la responsabilidad de filtrar por tenant es del PolicyStore / AuthorizationService,
        // cubierto por prueba de integración). Esta prueba documenta esa frontera de responsabilidad.
        var request = Request(tenantId: "tenant-b");
        var result = _sut.Evaluate(request, new[] { docTenantA });

        Assert.Equal(Effect.Permit, result.Effect); // documenta el comportamiento si se viola el contrato
    }

    [Fact]
    public void HourBetween_detecta_horario_de_madrugada_con_wraparound()
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "deny-madrugada", Priority = 10, Effect = Effect.Deny,
                    Actions = new[] { "*" }, ResourceTypes = new[] { "*" },
                    Conditions = new[] { new PolicyCondition { Source = ConditionSource.Context, Attribute = "hourUtc", Operator = ConditionOperator.HourBetween, Value = "22-4" } } }
        };
        var doc = Doc("t1", rules);

        var deny = _sut.Evaluate(Request(context: new Dictionary<string, object?> { ["hourUtc"] = 23 }), new[] { doc });
        var alsoDeny = _sut.Evaluate(Request(context: new Dictionary<string, object?> { ["hourUtc"] = 2 }), new[] { doc });
        var notDeny = _sut.Evaluate(Request(context: new Dictionary<string, object?> { ["hourUtc"] = 12 }), new[] { doc });

        Assert.Equal(Effect.Deny, deny.Effect);
        Assert.Equal(Effect.Deny, alsoDeny.Effect);
        Assert.Equal("default-effect", notDeny.Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Politica_deshabilitada_se_ignora(bool enabled)
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "permit-all", Priority = 10, Effect = Effect.Permit, Actions = new[] { "*" }, ResourceTypes = new[] { "*" } }
        };
        var doc = Doc("t1", rules) with { Enabled = enabled };

        var result = _sut.Evaluate(Request(), new[] { doc });

        Assert.Equal(enabled ? Effect.Permit : Effect.Deny, result.Effect);
    }

    /// <summary>
    /// Regresión de un bug REAL encontrado durante la verificación de esta entrega (ver
    /// AI-JOURNAL.md): si TODAS las políticas de un tenant están deshabilitadas, `orderedDocs`
    /// queda vacío y el código anterior accedía a `orderedDocs[0]` sin comprobar el conteo,
    /// lanzando `ArgumentOutOfRangeException` en vez de denegar. Una política mal apagada
    /// tumbaba el endpoint completo en lugar de responder Deny fail-closed.
    /// </summary>
    [Fact]
    public void Todas_las_politicas_deshabilitadas_no_crashea_devuelve_Deny()
    {
        var rules = new PolicyRule[]
        {
            new() { RuleId = "permit-all", Priority = 10, Effect = Effect.Permit, Actions = new[] { "*" }, ResourceTypes = new[] { "*" } }
        };
        var doc1 = Doc("t1", rules) with { Enabled = false };
        var doc2 = Doc("t1", rules) with { Enabled = false };

        var result = _sut.Evaluate(Request(), new[] { doc1, doc2 });

        Assert.Equal(Effect.Deny, result.Effect);
        Assert.Equal("all-policies-disabled", result.Reason);
    }
}
