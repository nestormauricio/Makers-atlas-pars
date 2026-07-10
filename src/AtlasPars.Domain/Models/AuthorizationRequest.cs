namespace AtlasPars.Domain.Models;

/// <summary>
/// Solicitud de autorización que recibe el endpoint POST /authorize.
/// Representa el modelo ABAC clásico: Actor, Recurso, Acción, Contexto.
/// </summary>
public sealed record AuthorizationRequest
{
    /// <summary>Identificador del tenant (squad) que hace la solicitud. Obligatorio para aislar políticas.</summary>
    public required string TenantId { get; init; }

    /// <summary>Quién ejecuta la acción (usuario, servicio, rol, atributos).</summary>
    public required Actor Actor { get; init; }

    /// <summary>Sobre qué recurso se actúa.</summary>
    public required Resource Resource { get; init; }

    /// <summary>Qué se quiere hacer (ej: "transfer.create", "pii.update", "admin.access").</summary>
    public required string Action { get; init; }

    /// <summary>Atributos contextuales: IP, hora, geolocalización, monto, canal, etc.</summary>
    public IReadOnlyDictionary<string, object?> Context { get; init; } = new Dictionary<string, object?>();

    /// <summary>Idempotency key opcional para que el cliente pueda reintentar de forma segura.</summary>
    public string? IdempotencyKey { get; init; }
}

public sealed record Actor
{
    public required string Id { get; init; }
    public IReadOnlyList<string> Roles { get; init; } = Array.Empty<string>();
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
}

public sealed record Resource
{
    public required string Type { get; init; }
    public required string Id { get; init; }
    public IReadOnlyDictionary<string, object?> Attributes { get; init; } = new Dictionary<string, object?>();
}
