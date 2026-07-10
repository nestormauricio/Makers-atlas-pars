using AtlasPars.Domain.Models;
using AtlasPars.Infrastructure.Signing;
using Xunit;

namespace AtlasPars.Tests.Unit;

public class HmacDecisionSignerTests
{
    private static AuthorizationDecision Decision(string reason = "rule:x") => new()
    {
        DecisionId = Guid.NewGuid(),
        TenantId = "t1",
        Effect = Effect.Permit,
        Reason = reason,
        PolicyId = "p1",
        PolicyVersion = 1,
        EvaluatedAtUtc = DateTimeOffset.UtcNow,
        RequestHash = "abc123"
    };

    private static IKeyProvider FixedKeyProvider(string keyId = "k1", string keyMaterial = "test-key-material-32-bytes-min!")
    {
        return new EnvKeyProvider(new Dictionary<string, string>
        {
            ["ATLAS_SIGNING_KEY_ID"] = keyId,
            ["ATLAS_SIGNING_KEY"] = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(keyMaterial))
        });
    }

    [Fact]
    public void Firma_tiene_formato_JWS_compacto_de_tres_partes()
    {
        var signer = new HmacDecisionSigner(FixedKeyProvider());
        var (signature, keyId) = signer.Sign(Decision());

        Assert.Equal(3, signature.Split('.').Length);
        Assert.Equal("k1", keyId);
    }

    [Fact]
    public void Verify_acepta_una_firma_valida()
    {
        var signer = new HmacDecisionSigner(FixedKeyProvider());
        var decision = Decision();
        var (signature, keyId) = signer.Sign(decision);

        Assert.True(signer.Verify(decision, signature, keyId));
    }

    /// <summary>
    /// Regresión de un bug de seguridad CRÍTICO encontrado durante la verificación de esta
    /// entrega (ver AI-JOURNAL.md): la implementación original de `Verify()` no comparaba
    /// realmente el parámetro `decision` contra la firma — recalculaba el HMAC usando el propio
    /// string de la firma como fuente de verdad, por lo que CUALQUIER firma válida pasaba
    /// `Verify()` para CUALQUIER decisión. Este test es la regresión de ese hallazgo.
    /// </summary>
    [Fact]
    public void Verify_rechaza_una_decision_manipulada_reason_distinto()
    {
        var signer = new HmacDecisionSigner(FixedKeyProvider());
        var original = Decision(reason: "rule:permit-role");
        var (signature, keyId) = signer.Sign(original);

        var tampered = original with { Reason = "rule:permit-admin-backdoor" };

        Assert.False(signer.Verify(tampered, signature, keyId));
    }

    [Fact]
    public void Verify_rechaza_firma_de_una_llave_desconocida()
    {
        var signer = new HmacDecisionSigner(FixedKeyProvider());
        var decision = Decision();
        var (signature, _) = signer.Sign(decision);

        Assert.False(signer.Verify(decision, signature, "llave-que-no-existe"));
    }

    [Fact]
    public void Dos_firmas_de_la_misma_decision_son_iguales_determinismo()
    {
        var signer = new HmacDecisionSigner(FixedKeyProvider());
        var decision = Decision();

        var (sig1, _) = signer.Sign(decision);
        var (sig2, _) = signer.Sign(decision);

        Assert.Equal(sig1, sig2);
    }
}
