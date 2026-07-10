using AtlasPars.Domain.Models;

namespace AtlasPars.Domain.Policies;

/// <summary>
/// Evalúa una AuthorizationRequest contra una lista de PolicyDocument y produce un veredicto.
/// Algoritmo de combinación: "deny-overrides" (estándar en motores ABAC/XACML): si alguna regla
/// aplicable evalúa a Deny, el resultado final es Deny, incluso si otras reglas dicen Permit.
/// Si ninguna regla aplica, se usa el DefaultEffect del documento de mayor prioridad (fail-closed).
/// Es intencionalmente puro y determinista: mismo input -> mismo output, sin I/O, para que sea
/// trivial de testear y de razonar bajo carga concurrente (sin estado compartido mutable).
/// </summary>
public sealed class PolicyEvaluator
{
    public EvaluationResult Evaluate(AuthorizationRequest request, IReadOnlyList<PolicyDocument> policies)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(policies);

        if (policies.Count == 0)
        {
            return new EvaluationResult(
                Effect.Deny,
                "no-policy-defined",
                PolicyId: "n/a",
                PolicyVersion: 0,
                Obligations: Array.Empty<string>());
        }

        var orderedDocs = policies.Where(p => p.Enabled).OrderByDescending(p => p.Version).ToList();

        PolicyRule? winningDenyRule = null;
        PolicyDocument? winningDenyDoc = null;
        PolicyRule? winningPermitRule = null;
        PolicyDocument? winningPermitDoc = null;
        PolicyRule? winningChallengeRule = null;
        PolicyDocument? winningChallengeDoc = null;

        foreach (var doc in orderedDocs)
        {
            foreach (var rule in doc.Rules.OrderByDescending(r => r.Priority))
            {
                if (!Matches(rule, request)) continue;

                switch (rule.Effect)
                {
                    case Effect.Deny when winningDenyRule is null || rule.Priority > winningDenyRule.Priority:
                        winningDenyRule = rule;
                        winningDenyDoc = doc;
                        break;
                    case Effect.Challenge when winningChallengeRule is null || rule.Priority > winningChallengeRule.Priority:
                        winningChallengeRule = rule;
                        winningChallengeDoc = doc;
                        break;
                    case Effect.Permit when winningPermitRule is null || rule.Priority > winningPermitRule.Priority:
                        winningPermitRule = rule;
                        winningPermitDoc = doc;
                        break;
                }
            }
        }

        // deny-overrides: Deny > Challenge > Permit
        if (winningDenyRule is not null)
        {
            return new EvaluationResult(Effect.Deny, $"rule:{winningDenyRule.RuleId}",
                winningDenyDoc!.PolicyId, winningDenyDoc.Version, winningDenyRule.Obligations);
        }

        if (winningChallengeRule is not null)
        {
            return new EvaluationResult(Effect.Challenge, $"rule:{winningChallengeRule.RuleId}",
                winningChallengeDoc!.PolicyId, winningChallengeDoc.Version, winningChallengeRule.Obligations);
        }

        if (winningPermitRule is not null)
        {
            return new EvaluationResult(Effect.Permit, $"rule:{winningPermitRule.RuleId}",
                winningPermitDoc!.PolicyId, winningPermitDoc.Version, winningPermitRule.Obligations);
        }

        // BUGFIX (hallado en verificación offline, ver AI-JOURNAL.md): si todas las políticas del
        // tenant están deshabilitadas (Enabled=false), `orderedDocs` queda vacío y no hay ningún
        // documento del cual tomar el DefaultEffect. Antes de este fix, acceder a orderedDocs[0]
        // lanzaba ArgumentOutOfRangeException (una política mal configurada tumbaba el endpoint
        // en vez de denegar). Ahora se trata igual que "no hay políticas": Deny fail-closed.
        if (orderedDocs.Count == 0)
        {
            return new EvaluationResult(
                Effect.Deny,
                "all-policies-disabled",
                PolicyId: "n/a",
                PolicyVersion: 0,
                Obligations: Array.Empty<string>());
        }

        var defaultDoc = orderedDocs[0];
        return new EvaluationResult(defaultDoc.DefaultEffect, "default-effect",
            defaultDoc.PolicyId, defaultDoc.Version, Array.Empty<string>());
    }

    private static bool Matches(PolicyRule rule, AuthorizationRequest request)
    {
        var actionMatches = rule.Actions.Contains("*") || rule.Actions.Contains(request.Action, StringComparer.OrdinalIgnoreCase);
        if (!actionMatches) return false;

        var resourceMatches = rule.ResourceTypes.Contains("*") ||
                               rule.ResourceTypes.Contains(request.Resource.Type, StringComparer.OrdinalIgnoreCase);
        if (!resourceMatches) return false;

        foreach (var condition in rule.Conditions)
        {
            if (!EvaluateCondition(condition, request)) return false;
        }

        return true;
    }

    private static bool EvaluateCondition(PolicyCondition condition, AuthorizationRequest request)
    {
        var actual = ResolveAttribute(condition.Source, condition.Attribute, request);

        return condition.Operator switch
        {
            ConditionOperator.Equals => CompareEquals(actual, condition.Value),
            ConditionOperator.NotEquals => !CompareEquals(actual, condition.Value),
            ConditionOperator.In => ToStringList(condition.Value).Contains(ToComparableString(actual), StringComparer.OrdinalIgnoreCase),
            ConditionOperator.NotIn => !ToStringList(condition.Value).Contains(ToComparableString(actual), StringComparer.OrdinalIgnoreCase),
            ConditionOperator.GreaterThan => ToDouble(actual) > ToDouble(condition.Value),
            ConditionOperator.GreaterOrEqual => ToDouble(actual) >= ToDouble(condition.Value),
            ConditionOperator.LessThan => ToDouble(actual) < ToDouble(condition.Value),
            ConditionOperator.LessOrEqual => ToDouble(actual) <= ToDouble(condition.Value),
            ConditionOperator.Contains => ToComparableString(actual).Contains(ToComparableString(condition.Value), StringComparison.OrdinalIgnoreCase),
            ConditionOperator.HourBetween => EvaluateHourBetween(actual, condition.Value),
            _ => false
        };
    }

    private static object? ResolveAttribute(ConditionSource source, string attribute, AuthorizationRequest request)
    {
        return source switch
        {
            ConditionSource.Actor => attribute switch
            {
                "id" => request.Actor.Id,
                "roles" => request.Actor.Roles,
                _ => request.Actor.Attributes.GetValueOrDefault(attribute)
            },
            ConditionSource.Resource => attribute switch
            {
                "id" => request.Resource.Id,
                "type" => request.Resource.Type,
                _ => request.Resource.Attributes.GetValueOrDefault(attribute)
            },
            ConditionSource.Context => request.Context.GetValueOrDefault(attribute),
            _ => null
        };
    }

    private static bool CompareEquals(object? actual, object expected)
        => string.Equals(ToComparableString(actual), ToComparableString(expected), StringComparison.OrdinalIgnoreCase);

    private static string ToComparableString(object? value) => value switch
    {
        null => string.Empty,
        IEnumerable<string> list => string.Join(",", list),
        _ => value.ToString() ?? string.Empty
    };

    private static IReadOnlyList<string> ToStringList(object value) => value switch
    {
        string s => s.Split(',', StringSplitOptions.TrimEntries),
        IEnumerable<string> strs => strs.ToList(),
        IEnumerable<object?> objs => objs.Select(o => o?.ToString() ?? string.Empty).ToList(),
        _ => new List<string> { value.ToString() ?? string.Empty }
    };

    private static double ToDouble(object? value) => value switch
    {
        null => 0,
        double d => d,
        int i => i,
        long l => l,
        string s when double.TryParse(s, out var parsed) => parsed,
        _ => throw new InvalidOperationException($"No se pudo convertir '{value}' a número para comparar la condición.")
    };

    /// <summary>
    /// Atajo para reglas de horario tipo "nadie puede aprobar entre 00:00 y 05:00 UTC".
    /// `actual` debe ser una hora (0-23) tomada del contexto (ej. context["hourUtc"]).
    /// `expected` debe ser "inicio-fin", ej. "0-5".
    /// </summary>
    private static bool EvaluateHourBetween(object? actual, object expected)
    {
        var hour = (int)ToDouble(actual);
        var range = ToComparableString(expected).Split('-', StringSplitOptions.TrimEntries);
        if (range.Length != 2) return false;
        var start = int.Parse(range[0]);
        var end = int.Parse(range[1]);
        return start <= end ? hour >= start && hour <= end : hour >= start || hour <= end;
    }
}

public sealed record EvaluationResult(
    Effect Effect,
    string Reason,
    string PolicyId,
    int PolicyVersion,
    IReadOnlyList<string> Obligations);
