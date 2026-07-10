using System.Text.Json;
using System.Text.Json.Serialization;
using AtlasPars.Domain.Abstractions;
using AtlasPars.Domain.Models;
using AtlasPars.Domain.Policies;

namespace AtlasPars.Infrastructure.Policies;

/// <summary>
/// Implementación de referencia de IPolicyStore que lee documentos de política desde archivos
/// JSON en disco (uno por tenant, ej. policies/tenant-payments.json). Es intencionalmente simple
/// para el PoC (ver ADR-0001): en producción cada squad publicaría sus políticas vía Git-ops
/// (pull request revisado + pipeline que valida el esquema antes de promover), o vía una tabla
/// versionada con auditoría de cambios. La interfaz IPolicyStore no cambia en ninguno de los casos.
/// Cachea en memoria con invalidación simple por timestamp de archivo para cumplir el SLA de
/// P95 &lt; 150ms sin pagar I/O de disco en cada request.
/// </summary>
public sealed class FileSystemPolicyStore : IPolicyStore
{
    private readonly string _policiesDirectory;
    private readonly Dictionary<string, (DateTime LastWriteUtc, List<PolicyDocument> Docs)> _cache = new();
    private readonly SemaphoreSlim _lock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true
    };

    public FileSystemPolicyStore(string policiesDirectory)
    {
        _policiesDirectory = policiesDirectory;
    }

    public async Task<IReadOnlyList<PolicyDocument>> GetActivePoliciesAsync(string tenantId, CancellationToken ct = default)
    {
        var path = Path.Combine(_policiesDirectory, $"{tenantId}.json");
        if (!File.Exists(path))
        {
            return Array.Empty<PolicyDocument>();
        }

        var lastWrite = File.GetLastWriteTimeUtc(path);

        await _lock.WaitAsync(ct);
        try
        {
            if (_cache.TryGetValue(tenantId, out var cached) && cached.LastWriteUtc == lastWrite)
            {
                return cached.Docs;
            }

            var json = await File.ReadAllTextAsync(path, ct);
            var docs = JsonSerializer.Deserialize<List<PolicyDocument>>(json, JsonOptions)
                       ?? new List<PolicyDocument>();
            docs = docs.Select(NormalizeDocument).ToList();

            _cache[tenantId] = (lastWrite, docs);
            return docs;
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// System.Text.Json deserializa PolicyCondition.Value (tipado `object`) como JsonElement, no
    /// como los tipos CLR planos (double/string/List) que espera PolicyEvaluator. Normalizamos
    /// aquí, en el adaptador de infraestructura que sabe de JSON, para que el dominio siga sin
    /// depender de System.Text.Json (mismo principio que en AuthorizeContracts.cs de la Api).
    /// </summary>
    private static PolicyDocument NormalizeDocument(PolicyDocument doc) => doc with
    {
        Rules = doc.Rules.Select(rule => rule with
        {
            Conditions = rule.Conditions.Select(c => c with { Value = NormalizeValue(c.Value) }).ToList()
        }).ToList()
    };

    private static object NormalizeValue(object value)
    {
        if (value is not JsonElement el) return value;

        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString() ?? string.Empty,
            JsonValueKind.Number => el.TryGetInt64(out var l) ? l : el.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => el.EnumerateArray().Select(e => NormalizeValue(e)).ToList(),
            _ => el.ToString()
        };
    }
}
