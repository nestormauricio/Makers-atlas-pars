using AtlasPars.Domain.Policies;

namespace AtlasPars.Domain.Abstractions;

/// <summary>
/// Puerto (hexagonal) hacia el almacén de políticas versionadas. Se implementa en Infrastructure
/// (por defecto: archivos JSON en disco / blob storage; en producción: tabla versionada o Git-ops).
/// </summary>
public interface IPolicyStore
{
    /// <summary>Devuelve las políticas activas (Enabled=true) de un tenant, ordenadas por prioridad.</summary>
    Task<IReadOnlyList<PolicyDocument>> GetActivePoliciesAsync(string tenantId, CancellationToken ct = default);
}
