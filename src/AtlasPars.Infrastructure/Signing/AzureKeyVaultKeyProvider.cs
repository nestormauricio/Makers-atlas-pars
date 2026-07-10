using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AtlasPars.Infrastructure.Signing;

/// <summary>
/// Proveedor de producción real: obtiene la llave de firma activa desde Azure Key Vault vía
/// identidad administrada (ver infra/main.tf: azurerm_user_assigned_identity + access policy).
/// Cada VERSIÓN del secreto en Key Vault es una llave rotada; el KeyId expuesto a
/// AuthorizationDecision.KeyId es la versión del secreto, lo que da trazabilidad nativa de
/// rotación sin lógica adicional (ver docs/RUNBOOK.md "Rotación de llaves").
///
/// Cachea la llave activa en memoria con una expiración corta (5 min) para no golpear Key Vault
/// en cada request y cumplir el SLA de P95 &lt; 150ms; las llaves históricas (para `Verify` de
/// decisiones antiguas) se cachean indefinidamente en el proceso una vez recuperadas.
///
/// NOTA DE VERIFICACIÓN (ver AI-JOURNAL.md): esta clase no se pudo compilar/ejecutar en el sandbox
/// de desarrollo por falta de acceso de red a nuget.org para restaurar los paquetes
/// Azure.Security.KeyVault.Secrets / Azure.Identity. Se revisó manualmente contra la
/// documentación pública del SDK (misma superficie de API que EnvKeyProvider vía IKeyProvider,
/// intercambiable sin tocar HmacDecisionSigner). Debe validarse con `dotnet build` + una prueba
/// de integración real contra un Key Vault de desarrollo antes de activar
/// `Atlas:KeyProvider=AzureKeyVault` en un despliegue real.
/// </summary>
public sealed class AzureKeyVaultKeyProvider : IKeyProvider, IDisposable
{
    private readonly Azure.Security.KeyVault.Secrets.SecretClient _client;
    private readonly string _secretName;
    private readonly ILogger<AzureKeyVaultKeyProvider> _logger;
    private readonly ConcurrentDictionary<string, byte[]> _historicalKeysCache = new();
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private static readonly TimeSpan ActiveKeyCacheTtl = TimeSpan.FromMinutes(5);

    private (string KeyId, byte[] Key)? _activeCached;
    private DateTimeOffset _activeCachedAtUtc = DateTimeOffset.MinValue;

    public AzureKeyVaultKeyProvider(IConfiguration configuration, ILogger<AzureKeyVaultKeyProvider> logger)
    {
        _logger = logger;
        var vaultUri = configuration["Atlas:KeyVaultUri"]
            ?? throw new InvalidOperationException(
                "Atlas:KeyVaultUri es obligatorio cuando Atlas:KeyProvider=AzureKeyVault. " +
                "Coincide con el output 'key_vault_uri' de infra/main.tf.");
        _secretName = configuration["Atlas:SigningKeySecretName"] ?? "atlas-signing-key";

        // DefaultAzureCredential: Managed Identity en Azure (ver azurerm_user_assigned_identity en
        // infra/main.tf), `az login` en desarrollo local. Nunca credenciales embebidas en el repo.
        _client = new Azure.Security.KeyVault.Secrets.SecretClient(
            new Uri(vaultUri), new Azure.Identity.DefaultAzureCredential());
    }

    public (string KeyId, byte[] Key) GetActiveSigningKey()
    {
        if (_activeCached is { } cached && DateTimeOffset.UtcNow - _activeCachedAtUtc < ActiveKeyCacheTtl)
            return cached;

        _refreshLock.Wait();
        try
        {
            if (_activeCached is { } stillCached && DateTimeOffset.UtcNow - _activeCachedAtUtc < ActiveKeyCacheTtl)
                return stillCached;

            var secret = _client.GetSecret(_secretName).Value;
            var keyId = secret.Properties.Version; // versión de Key Vault == KeyId de la decisión firmada
            var keyBytes = Convert.FromBase64String(secret.Value);

            _historicalKeysCache[keyId] = keyBytes;
            _activeCached = (keyId, keyBytes);
            _activeCachedAtUtc = DateTimeOffset.UtcNow;
            _logger.LogInformation("Llave de firma activa refrescada desde Key Vault. KeyId={KeyId}", keyId);
            return _activeCached.Value;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public bool TryGetKey(string keyId, out byte[] key)
    {
        if (_historicalKeysCache.TryGetValue(keyId, out var cached))
        {
            key = cached;
            return true;
        }

        try
        {
            var secret = _client.GetSecret(_secretName, keyId).Value; // versión específica -> llave histórica
            key = Convert.FromBase64String(secret.Value);
            _historicalKeysCache[keyId] = key;
            return true;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger.LogWarning(
                "KeyId '{KeyId}' no encontrado en Key Vault (¿purgado tras la retención?). " +
                "Ver docs/RUNBOOK.md 'Rotación de llaves' sobre la política de retención de versiones.",
                keyId);
            key = Array.Empty<byte>();
            return false;
        }
    }

    public void Dispose() => _refreshLock.Dispose();
}
