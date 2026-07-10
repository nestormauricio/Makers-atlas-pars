using AtlasPars.Domain.Models;

namespace AtlasPars.Domain.Abstractions;

/// <summary>
/// Puerto hacia el almacén de auditoría. Append-only por diseño: nunca se actualiza ni borra una
/// decisión ya escrita (ver ADR-0003). Cada registro incluye la decisión firmada.
/// </summary>
public interface IAuditStore
{
    Task AppendAsync(AuditRecord record, CancellationToken ct = default);

    Task<AuditRecord?> GetByDecisionIdAsync(Guid decisionId, CancellationToken ct = default);

    Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken ct = default);
}

public sealed record AuditRecord
{
    public required Guid DecisionId { get; init; }
    public required string TenantId { get; init; }
    public required AuthorizationRequest Request { get; init; }
    public required AuthorizationDecision Decision { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed record AuditQuery
{
    public string? TenantId { get; init; }
    public Effect? Effect { get; init; }
    public DateTimeOffset? FromUtc { get; init; }
    public DateTimeOffset? ToUtc { get; init; }
    public int Limit { get; init; } = 100;
}
