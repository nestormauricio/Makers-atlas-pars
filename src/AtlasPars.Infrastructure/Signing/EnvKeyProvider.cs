using System.Text;

namespace AtlasPars.Infrastructure.Signing;

/// <summary>
/// Proveedor de llaves para el PoC: lee la llave activa desde variable de entorno
/// ATLAS_SIGNING_KEY (base64) y su id desde ATLAS_SIGNING_KEY_ID. En producción esto se
/// reemplaza por un adaptador a Azure Key Vault que además soporte múltiples llaves vigentes
/// simultáneamente para permitir rotación sin downtime (ver RUNBOOK.md).
/// </summary>
public sealed class EnvKeyProvider : IKeyProvider
{
    private readonly Dictionary<string, byte[]> _keys;
    private readonly string _activeKeyId;

    public EnvKeyProvider(IDictionary<string, string>? overrideEnv = null)
    {
        var env = overrideEnv is not null
            ? new Dictionary<string, string>(overrideEnv)
            : Environment.GetEnvironmentVariables()
                .Cast<System.Collections.DictionaryEntry>()
                .ToDictionary(e => (string)e.Key, e => (string)e.Value!);

        _activeKeyId = env.GetValueOrDefault("ATLAS_SIGNING_KEY_ID") ?? "dev-key-1";
        var keyB64 = env.GetValueOrDefault("ATLAS_SIGNING_KEY");

        _keys = new Dictionary<string, byte[]>();
        if (!string.IsNullOrWhiteSpace(keyB64))
        {
            _keys[_activeKeyId] = Convert.FromBase64String(keyB64);
        }
        else
        {
            // Fallback SOLO para desarrollo local sin secretos configurados.
            // Nunca usar en un ambiente desplegado (ver THREAT-MODEL.md T-04).
            _keys[_activeKeyId] = Encoding.UTF8.GetBytes("dev-only-insecure-key-do-not-deploy-32b");
        }
    }

    public (string KeyId, byte[] Key) GetActiveSigningKey() => (_activeKeyId, _keys[_activeKeyId]);

    public bool TryGetKey(string keyId, out byte[] key)
    {
        if (_keys.TryGetValue(keyId, out var k))
        {
            key = k;
            return true;
        }
        key = Array.Empty<byte>();
        return false;
    }
}
