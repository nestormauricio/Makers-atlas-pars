using AtlasPars.Domain.Models;

namespace AtlasPars.Domain.Policies;

/// <summary>
/// Documento de política declarativa versionado. Formato propio JSON, deliberadamente simple
/// (ver ADR-0001 para la justificación frente a Rego/OPA completo).
/// Un tenant puede tener varias políticas; se evalúan en orden de prioridad y aplica
/// "deny-overrides": si cualquier regla aplicable dice Deny, gana Deny. Si ninguna regla aplica,
/// el default es Deny (fail-closed), salvo que el documento declare DefaultEffect distinto.
/// </summary>
public sealed record PolicyDocument
{
    public required string PolicyId { get; init; }
    public required int Version { get; init; }
    public required string TenantId { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<PolicyRule> Rules { get; init; }
    public Effect DefaultEffect { get; init; } = Effect.Deny;
    public bool Enabled { get; init; } = true;
}

public sealed record PolicyRule
{
    public required string RuleId { get; init; }
    public required int Priority { get; init; }
    public required Effect Effect { get; init; }

    /// <summary>Acciones a las que aplica la regla. Soporta "*" como comodín.</summary>
    public required IReadOnlyList<string> Actions { get; init; }

    /// <summary>Tipos de recurso a los que aplica. Soporta "*".</summary>
    public required IReadOnlyList<string> ResourceTypes { get; init; }

    /// <summary>Condiciones ABAC combinadas con AND. Cada condición evalúa un atributo del actor,
    /// recurso o contexto contra un operador y un valor.</summary>
    public IReadOnlyList<PolicyCondition> Conditions { get; init; } = Array.Empty<PolicyCondition>();

    public IReadOnlyList<string> Obligations { get; init; } = Array.Empty<string>();

    public string? Description { get; init; }
}

public enum ConditionSource
{
    Actor,
    Resource,
    Context
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    In,
    NotIn,
    GreaterThan,
    GreaterOrEqual,
    LessThan,
    LessOrEqual,
    Contains,
    HourBetween // atajo frecuente para reglas de horario (ej. madrugada)
}

public sealed record PolicyCondition
{
    public required ConditionSource Source { get; init; }
    public required string Attribute { get; init; }
    public required ConditionOperator Operator { get; init; }

    /// <summary>Valor de comparación. Para In/NotIn/HourBetween se espera una lista/rango serializado.</summary>
    public required object Value { get; init; }
}
