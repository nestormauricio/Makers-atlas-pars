using System.Text.Json;
using System.Text.Json.Serialization;
using AtlasPars.Domain.Abstractions;
using AtlasPars.Domain.Models;

namespace AtlasPars.Infrastructure.Audit;

/// <summary>
/// Almacén de auditoría append-only basado en archivos JSON Lines (un archivo por tenant,
/// una línea = un AuditRecord). DECISIÓN DE DISEÑO (ver ADR-0003): para el PoC evitamos traer
/// un motor de base de datos completo (Postgres/EventStoreDB) porque el objetivo aquí es
/// demostrar el contrato (IAuditStore) y las garantías (append-only, nunca se sobreescribe),
/// no operar una BD real. El archivo se abre en modo Append exclusivo por escritura, y cada
/// línea es atómica a nivel de SO para escrituras < 4KB, lo cual es suficiente para el PoC bajo
/// concurrencia moderada. En producción: Postgres con tabla append-only (sin UPDATE/DELETE
/// permitidos vía GRANT) o un event store real, particionado por tenant.
/// </summary>
public sealed class JsonlAuditStore : IAuditStore
{
    private readonly string _auditDirectory;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public JsonlAuditStore(string auditDirectory)
    {
        _auditDirectory = auditDirectory;
        Directory.CreateDirectory(_auditDirectory);
    }

    public async Task AppendAsync(AuditRecord record, CancellationToken ct = default)
    {
        var path = FileFor(record.TenantId);
        var line = JsonSerializer.Serialize(record, JsonOptions);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            await using var writer = new StreamWriter(stream);
            await writer.WriteLineAsync(line);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<AuditRecord?> GetByDecisionIdAsync(Guid decisionId, CancellationToken ct = default)
    {
        if (!Directory.Exists(_auditDirectory)) return null;

        foreach (var file in Directory.EnumerateFiles(_auditDirectory, "*.jsonl"))
        {
            await foreach (var record in ReadAllAsync(file, ct))
            {
                if (record.DecisionId == decisionId) return record;
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<AuditRecord>> QueryAsync(AuditQuery query, CancellationToken ct = default)
    {
        var results = new List<AuditRecord>();
        if (!Directory.Exists(_auditDirectory)) return results;

        var files = query.TenantId is not null
            ? new[] { FileFor(query.TenantId) }.Where(File.Exists)
            : Directory.EnumerateFiles(_auditDirectory, "*.jsonl");

        foreach (var file in files)
        {
            await foreach (var record in ReadAllAsync(file, ct))
            {
                if (query.Effect is not null && record.Decision.Effect != query.Effect) continue;
                if (query.FromUtc is not null && record.RecordedAtUtc < query.FromUtc) continue;
                if (query.ToUtc is not null && record.RecordedAtUtc > query.ToUtc) continue;

                results.Add(record);
                if (results.Count >= query.Limit) return results;
            }
        }
        return results;
    }

    private string FileFor(string tenantId) => Path.Combine(_auditDirectory, $"{tenantId}.jsonl");

    private static async IAsyncEnumerable<AuditRecord> ReadAllAsync(
        string file, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(file);
        string? line;
        while ((line = await reader.ReadLineAsync(ct)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            AuditRecord? record = null;
            try
            {
                record = JsonSerializer.Deserialize<AuditRecord>(line, JsonOptions);
            }
            catch (JsonException)
            {
                // Línea corrupta (ej. escritura interrumpida a mitad). Se ignora y se continúa;
                // en producción esto dispararía una alerta (integridad de auditoría).
            }
            if (record is not null) yield return record;
        }
    }
}
