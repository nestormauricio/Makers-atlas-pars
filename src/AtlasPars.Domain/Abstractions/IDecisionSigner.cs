using AtlasPars.Domain.Models;

namespace AtlasPars.Domain.Abstractions;

/// <summary>
/// Puerto hacia el firmante criptográfico. La implementación real vive en Infrastructure y
/// obtiene la llave activa desde el gestor de secretos (Key Vault / Secrets Manager / KMS).
/// </summary>
public interface IDecisionSigner
{
    /// <summary>Firma la decisión y devuelve (signature, keyId). No muta el objeto de dominio.</summary>
    (string Signature, string KeyId) Sign(AuthorizationDecision decision);

    /// <summary>Verifica que una firma corresponda a la decisión y a una llave conocida (para auditoría/replay).</summary>
    bool Verify(AuthorizationDecision decision, string signature, string keyId);
}
