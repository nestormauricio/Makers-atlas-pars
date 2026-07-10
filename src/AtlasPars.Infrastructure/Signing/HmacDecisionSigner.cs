using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AtlasPars.Domain.Abstractions;
using AtlasPars.Domain.Models;

namespace AtlasPars.Infrastructure.Signing;

/// <summary>
/// Firma cada decisión como un JWS Compact Serialization (RFC 7515) usando HMAC-SHA256.
/// DECISIÓN DE DISEÑO (ver ADR-0002): en vez de traer una librería JWT completa, implementamos
/// a mano SOLO la serialización compacta (base64url(header) + "." + base64url(payload) + "." +
/// base64url(HMACSHA256(header.payload))), usando exclusivamente primitivas criptográficas del
/// BCL (System.Security.Cryptography.HMACSHA256). No inventamos el algoritmo criptográfico
/// (eso sería "rolled-your-own crypto", prohibido) — sólo el envoltorio de serialización, que es
/// trivial y auditable en ~80 líneas. Esto reduce superficie de dependencias de terceros en el
/// componente más sensible del sistema (la firma) a costa de no soportar todo el estándar JOSE
/// (sin RS256/ES256 aquí). Para producción multi-tenant con llaves asimétricas por squad,
/// migrar a RS256/ES256 vía Azure Key Vault (ver RUNBOOK.md "Rotación de llaves").
/// </summary>
public sealed class HmacDecisionSigner : IDecisionSigner
{
    private readonly IKeyProvider _keyProvider;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public HmacDecisionSigner(IKeyProvider keyProvider)
    {
        _keyProvider = keyProvider;
    }

    public (string Signature, string KeyId) Sign(AuthorizationDecision decision)
    {
        var (keyId, key) = _keyProvider.GetActiveSigningKey();
        var signature = ComputeJws(decision, keyId, key);
        return (signature, keyId);
    }

    public bool Verify(AuthorizationDecision decision, string signature, string keyId)
    {
        if (!_keyProvider.TryGetKey(keyId, out var key)) return false;

        var parts = signature.Split('.');
        if (parts.Length != 3) return false;

        // BUGFIX CRÍTICO (hallado en verificación offline, ver AI-JOURNAL.md): la implementación
        // original recalculaba el HMAC sobre las partes header/payload TOMADAS DEL PROPIO STRING
        // `signature`, sin comparar nunca contra el parámetro `decision`. Eso significaba que
        // CUALQUIER firma válida (producida por Sign() para cualquier decisión) pasaba Verify()
        // para CUALQUIER `decision` que se le pasara — la firma nunca quedaba ligada al contenido
        // verificado, anulando la garantía de integridad/no-repudio (ver ADR-0002, THREAT-MODEL T-03).
        // El fix: recomputar el JWS completo A PARTIR del `decision` recibido y compararlo en
        // tiempo constante contra la firma dada. Así cualquier campo alterado de `decision`
        // produce una firma recalculada distinta, y Verify() la rechaza correctamente.
        var expectedSignature = ComputeJws(decision, keyId, key);
        var expectedBytes = Encoding.UTF8.GetBytes(expectedSignature);
        var actualBytes = Encoding.UTF8.GetBytes(signature);

        if (expectedBytes.Length != actualBytes.Length) return false;
        return CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string ComputeJws(AuthorizationDecision decision, string keyId, byte[] key)
    {
        var header = new { alg = "HS256", typ = "JWS", kid = keyId };
        var payload = new
        {
            decision.DecisionId,
            decision.TenantId,
            effect = decision.Effect.ToString(),
            decision.Reason,
            decision.PolicyId,
            decision.PolicyVersion,
            evaluatedAt = decision.EvaluatedAtUtc.ToUnixTimeSeconds(),
            decision.RequestHash
        };

        var headerB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));
        var payloadB64 = Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions));
        var signingInput = $"{headerB64}.{payloadB64}";
        var mac = ComputeHmac(signingInput, key);
        var sigB64 = Base64UrlEncode(mac);

        return $"{signingInput}.{sigB64}";
    }

    private static byte[] ComputeHmac(string input, byte[] key)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(input));
    }

    private static string Base64UrlEncode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }
}

/// <summary>
/// Abstrae de dónde salen las llaves de firma (Key Vault en producción, variable de entorno o
/// archivo local en el PoC). Ver ADR-0002 y RUNBOOK.md.
/// </summary>
public interface IKeyProvider
{
    (string KeyId, byte[] Key) GetActiveSigningKey();
    bool TryGetKey(string keyId, out byte[] key);
}
