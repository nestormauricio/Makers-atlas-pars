using System.Text.Json;
using AtlasPars.Domain.Models;

namespace AtlasPars.Api.Endpoints;

/// <summary>DTO de entrada HTTP. Se mapea a AuthorizationRequest del dominio en el endpoint.</summary>
public sealed record AuthorizeHttpRequest(
    string TenantId,
    ActorDto Actor,
    ResourceDto Resource,
    string Action,
    Dictionary<string, object?>? Context,
    string? IdempotencyKey);

public sealed record ActorDto(string Id, List<string>? Roles, Dictionary<string, object?>? Attributes);
public sealed record ResourceDto(string Type, string Id, Dictionary<string, object?>? Attributes);

public sealed record AuthorizeHttpResponse(
    Guid DecisionId,
    string Effect,
    string Reason,
    string PolicyId,
    int PolicyVersion,
    DateTimeOffset EvaluatedAtUtc,
    string Signature,
    string KeyId,
    IReadOnlyList<string> Obligations);

public static class Mapping
{
    public static AuthorizationRequest ToDomain(this AuthorizeHttpRequest dto) => new()
    {
        TenantId = dto.TenantId,
        Actor = new Actor
        {
            Id = dto.Actor.Id,
            Roles = dto.Actor.Roles ?? new List<string>(),
            Attributes = Normalize(dto.Actor.Attributes)
        },
        Resource = new Resource
        {
            Type = dto.Resource.Type,
            Id = dto.Resource.Id,
            Attributes = Normalize(dto.Resource.Attributes)
        },
        Action = dto.Action,
        Context = Normalize(dto.Context),
        IdempotencyKey = dto.IdempotencyKey
    };

    /// <summary>
    /// System.Text.Json deserializa propiedades tipadas como `object?` como JsonElement (no como
    /// double/string/bool nativos). El motor de políticas del dominio trabaja con tipos CLR planos
    /// a propósito (para no acoplar el dominio a System.Text.Json), así que normalizamos aquí,
    /// en el borde HTTP, que es donde corresponde traducir el formato de wire a tipos de dominio.
    /// </summary>
    private static Dictionary<string, object?> Normalize(Dictionary<string, object?>? source)
    {
        var result = new Dictionary<string, object?>();
        if (source is null) return result;
        foreach (var (key, value) in source)
        {
            result[key] = NormalizeValue(value);
        }
        return result;
    }

    private static object? NormalizeValue(object? value)
    {
        if (value is not JsonElement el) return value;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Null => null,
            JsonValueKind.Array => el.EnumerateArray().Select(e => NormalizeValue(e)).ToList(),
            JsonValueKind.Object => el.EnumerateObject().ToDictionary(p => p.Name, p => NormalizeValue(p.Value)),
            _ => el.ToString()
        };
    }

    public static AuthorizeHttpResponse ToHttp(this AuthorizationDecision d) => new(
        d.DecisionId, d.Effect.ToString(), d.Reason, d.PolicyId, d.PolicyVersion,
        d.EvaluatedAtUtc, d.Signature ?? string.Empty, d.KeyId ?? string.Empty, d.Obligations);
}
