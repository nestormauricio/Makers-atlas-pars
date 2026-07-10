using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AtlasPars.Tests.Integration;

/// <summary>
/// Verifica la guardia de arranque fail-fast (ver Program.cs y THREAT-MODEL.md T-04): el proceso
/// NUNCA debe levantar en ASPNETCORE_ENVIRONMENT=Production con el proveedor de llaves por
/// variable de entorno si no hay ATLAS_SIGNING_KEY configurada explícitamente. Antes de esta
/// guardia, ese escenario arrancaba "silenciosamente" con la llave insegura de desarrollo.
/// </summary>
public class ProductionKeyGuardTests
{
    [Fact]
    public void Arranque_falla_en_Production_sin_llave_de_firma_configurada()
    {
        var previousKey = Environment.GetEnvironmentVariable("ATLAS_SIGNING_KEY");
        try
        {
            Environment.SetEnvironmentVariable("ATLAS_SIGNING_KEY", null);

            var policiesDir = Path.Combine(Path.GetTempPath(), "atlas-guard-policies-" + Guid.NewGuid());
            var auditDir = Path.Combine(Path.GetTempPath(), "atlas-guard-audit-" + Guid.NewGuid());
            Directory.CreateDirectory(policiesDir);

            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["Atlas:PoliciesDirectory"] = policiesDir,
                        ["Atlas:AuditDirectory"] = auditDir,
                        ["Atlas:KeyProvider"] = "EnvVar"
                    });
                });
            });

            // El fallo ocurre durante la construcción del host (Program.cs se ejecuta al primer
            // acceso al servidor de pruebas); WebApplicationFactory envuelve esa excepción.
            var ex = Assert.ThrowsAny<Exception>(() => factory.Server);
            Assert.Contains("Production", CollectMessages(ex));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ATLAS_SIGNING_KEY", previousKey);
        }
    }

    private static string CollectMessages(Exception ex)
    {
        var messages = new List<string>();
        var current = ex;
        while (current is not null)
        {
            messages.Add(current.Message);
            current = current.InnerException;
        }
        return string.Join(" | ", messages);
    }
}
