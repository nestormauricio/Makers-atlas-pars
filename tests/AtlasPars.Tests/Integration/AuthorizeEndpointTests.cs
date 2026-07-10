using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AtlasPars.Tests.Integration;

/// <summary>
/// Prueba end-to-end obligatoria (ver sección 3.2 de la prueba técnica): levanta la Api completa
/// en memoria (TestServer), apunta a un directorio de políticas de prueba y golpea /authorize
/// por HTTP real, verificando el contrato completo (evaluación + firma + respuesta).
/// </summary>
public class AuthorizeEndpointTests : IClassFixture<AuthorizeEndpointTests.ApiFactory>
{
    private readonly HttpClient _client;

    public AuthorizeEndpointTests(ApiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Authorize_permite_operador_con_rol_correcto()
    {
        var response = await _client.PostAsJsonAsync("/authorize", new
        {
            tenantId = "test-tenant",
            actor = new { id = "u1", roles = new[] { "operator" } },
            resource = new { type = "account", id = "acc-1" },
            action = "transfer.create",
            context = new Dictionary<string, object> { ["amount"] = 100 }
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Permit", body.GetProperty("effect").GetString());
        Assert.False(string.IsNullOrEmpty(body.GetProperty("signature").GetString()));
    }

    [Fact]
    public async Task Authorize_deniega_por_defecto_sin_reglas_aplicables()
    {
        var response = await _client.PostAsJsonAsync("/authorize", new
        {
            tenantId = "test-tenant",
            actor = new { id = "u2", roles = Array.Empty<string>() },
            resource = new { type = "account", id = "acc-1" },
            action = "transfer.create",
            context = new Dictionary<string, object>()
        });

        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("Deny", body.GetProperty("effect").GetString());
    }

    [Fact]
    public async Task Authorize_sin_tenantId_devuelve_400()
    {
        var response = await _client.PostAsJsonAsync("/authorize", new
        {
            tenantId = "",
            actor = new { id = "u1", roles = Array.Empty<string>() },
            resource = new { type = "account", id = "acc-1" },
            action = "transfer.create",
            context = new Dictionary<string, object>()
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Health_responde_ok()
    {
        var response = await _client.GetAsync("/health");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Decision_queda_auditada_y_es_consultable()
    {
        var authResponse = await _client.PostAsJsonAsync("/authorize", new
        {
            tenantId = "test-tenant",
            actor = new { id = "u1", roles = new[] { "operator" } },
            resource = new { type = "account", id = "acc-1" },
            action = "transfer.create",
            context = new Dictionary<string, object>()
        });
        var decision = await authResponse.Content.ReadFromJsonAsync<JsonElement>();
        var decisionId = decision.GetProperty("decisionId").GetString();

        var auditResponse = await _client.GetAsync($"/audit/{decisionId}");

        auditResponse.EnsureSuccessStatusCode();
    }

    public class ApiFactory : WebApplicationFactory<Program>
    {
        private readonly string _policiesDir = Path.Combine(Path.GetTempPath(), "atlas-test-policies-" + Guid.NewGuid());
        private readonly string _auditDir = Path.Combine(Path.GetTempPath(), "atlas-test-audit-" + Guid.NewGuid());

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Fijamos el entorno explícitamente: estas pruebas verifican el comportamiento
            // funcional (evaluar/firmar/auditar), no la guardia de arranque de producción
            // (ver ProductionKeyGuardTests, que sí fija "Production" a propósito). Sin esto,
            // el entorno por defecto de ASP.NET Core cuando ASPNETCORE_ENVIRONMENT no está
            // configurado es "Production", lo que dispararía la guardia fail-fast de Program.cs
            // y tumbaría estas pruebas por una razón ajena a lo que verifican.
            builder.UseEnvironment("Development");

            Directory.CreateDirectory(_policiesDir);
            File.WriteAllText(Path.Combine(_policiesDir, "test-tenant.json"), """
            [
              {
                "policyId": "test-policy",
                "version": 1,
                "tenantId": "test-tenant",
                "description": "Política de prueba de integración",
                "defaultEffect": "Deny",
                "enabled": true,
                "rules": [
                  {
                    "ruleId": "permit-operator",
                    "priority": 10,
                    "effect": "Permit",
                    "actions": ["transfer.create"],
                    "resourceTypes": ["account"],
                    "conditions": [
                      { "source": "Actor", "attribute": "roles", "operator": "Contains", "value": "operator" }
                    ]
                  }
                ]
              }
            ]
            """);

            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Atlas:PoliciesDirectory"] = _policiesDir,
                    ["Atlas:AuditDirectory"] = _auditDir
                });
            });
        }
    }
}
