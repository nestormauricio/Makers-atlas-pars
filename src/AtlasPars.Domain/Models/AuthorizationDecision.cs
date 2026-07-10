namespace AtlasPars.Domain.Models;

public enum Effect
{
    Permit,
    Deny,
    Challenge
}

/// <summary>
/// Resultado de evaluar una AuthorizationRequest contra el conjunto de políticas vigente.
/// Se firma criptográficamente antes de devolverse y antes de persistirse en la auditoría.
/// </summary>
public sealed record AuthorizationDecision
{
    public required Guid DecisionId { get; init; }
    public required string TenantId { get; init; }
    public required Effect Effect { get; init; }
    public required string Reason { get; init; }
    public required string PolicyId { get; init; }
    public required int PolicyVersion { get; init; }
    public required DateTimeOffset EvaluatedAtUtc { get; init; }
    public required string RequestHash { get; init; }

    /// <summary>Firma JWS compacta (header.payload.signature) sobre la decisión canónica.</summary>
    public string? Signature { get; set; }

    /// <summary>Identificador de la llave usada para firmar (para soportar rotación, ver RUNBOOK).</summary>
    public string? KeyId { get; set; }

    /// <summary>Obligaciones adicionales cuando el efecto es Challenge (ej: MFA, aprobación manual).</summary>
    public IReadOnlyList<string> Obligations { get; init; } = Array.Empty<string>();
}
