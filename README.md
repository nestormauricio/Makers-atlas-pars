# Atlas PARS — Policy Access to Sensitive Resources

Servicio de plataforma para autorizar operaciones críticas (transferencias, cambios de datos
personales, accesos administrativos) vía políticas ABAC declarativas, con decisiones firmadas
criptográficamente y auditoría append-only. Construido para la prueba técnica "Atlas Forge" de
Makers (ver `docs/` para el contexto completo del reto).

## Requisitos

- Para correr con Docker (recomendado): **Docker** y **Docker Compose** únicamente.
- Para correr sin Docker: **.NET 8 SDK**.
- Ninguna base de datos ni servicio externo es obligatorio para el PoC (auditoría y políticas
  viven en el propio filesystem, ver sección "Ubicación de políticas y auditoría" más abajo).

## Qué SÍ hace este PoC

- `POST /authorize`: recibe actor/recurso/acción/contexto, evalúa políticas versionadas por
  tenant (algoritmo deny-overrides), devuelve `PERMIT | DENY | CHALLENGE` firmado (JWS/HS256).
- `GET /audit/{decisionId}`: consulta el registro de auditoría append-only de una decisión.
- `GET /health`: health check compuesto y no trivial — valida el almacén de políticas, que el
  audit store acepte escrituras, y que el firmante efectivamente firme/verifique (no solo que el
  proceso esté vivo).
- Multi-tenant por particionamiento lógico, con guard defensivo de aislamiento (ver ADR-0003).
- Rotación de llaves de firma real vía Azure Key Vault (`AzureKeyVaultKeyProvider`, ver ADR-0004),
  con `EnvKeyProvider` como alternativa solo para desarrollo (bloqueada en producción por una
  guardia de arranque fail-fast).
- Observabilidad real: OpenTelemetry SDK configurado (no solo la API de `Meter`) con trazas
  distribuidas (`ActivitySource` con spans `Authorize → policy.evaluate/decision.sign/audit.append`),
  métricas y logs estructurados correlacionados por `TraceId`.

## Qué NO hace (alcance recortado a propósito, ver `BACKLOG.md`)

- No implementa autenticación del llamador (el `tenantId` se confía del body en este PoC — ver
  `docs/THREAT-MODEL.md`, T-01, gap documentado explícitamente).
- No usa una base de datos real para auditoría (usa archivos JSONL append-only; el contrato
  `IAuditStore` está listo para un reemplazo por Postgres/EventStoreDB sin tocar dominio).
- No implementa el componente disruptivo opcional de la sección 7 de la prueba (se priorizó
  verificar sólidamente lo obligatorio — ver `AI-JOURNAL.md`, sección 7).

## Cómo correrlo localmente (un solo comando)

```bash
./run.sh
# equivalente manual:
cp .env.example .env   # define tu propia llave de firma de desarrollo
export $(grep -v '^#' .env | xargs)
docker compose -f deploy/docker-compose.yml up --build
```

Luego:
```bash
curl -X POST http://localhost:8080/authorize \
  -H "Content-Type: application/json" \
  -d '{
    "tenantId": "payments-squad",
    "actor": { "id": "u1", "roles": ["payments-operator"] },
    "resource": { "type": "account", "id": "acc-1" },
    "action": "transfer.create",
    "context": { "originCountry": "CO", "amountCop": 1000 }
  }'
```

Políticas de ejemplo incluidas en `policies/payments-squad.json` (deny por país sancionado,
challenge por monto alto con MFA, permit por rol).

Verificar el health check:
```bash
curl -s http://localhost:8080/health | jq
# {"status":"Healthy","results":{"policies_directory":{"status":"Healthy",...},
#   "audit_store_writable":{"status":"Healthy"}, "signing_service":{"status":"Healthy",...}}}
```

## Ubicación de políticas y auditoría

- **Políticas**: `policies/*.json` (montado en el contenedor vía `Atlas__PoliciesDirectory`,
  por defecto `/app/policies` en Docker). Un archivo por tenant; ver `policies/payments-squad.json`
  como ejemplo. Cada política declara `tenantId`, `version` y `defaultEffect` (ver ADR-0001).
- **Auditoría**: JSON Lines append-only en `Atlas__AuditDirectory` (por defecto `/data/audit` en
  Docker, montado como volumen para persistir entre reinicios — ver `deploy/docker-compose.yml`).
  Consultable vía `GET /audit/{decisionId}` o inspeccionando el archivo directamente
  (`cat audit-data/*.jsonl | jq`).

## Cómo correrlo sin Docker

```bash
export ATLAS_SIGNING_KEY=$(echo -n "una-llave-de-desarrollo-cualquiera-32b" | base64)
export Atlas__PoliciesDirectory=$(pwd)/policies
export Atlas__AuditDirectory=/tmp/atlas-audit
dotnet run --project src/AtlasPars.Api
```

## Cómo correr las pruebas

```bash
dotnet test
```
**Nota de transparencia** (ver `AI-JOURNAL.md` para el detalle completo): el proyecto de
pruebas usa xUnit + `Microsoft.AspNetCore.Mvc.Testing` + coverlet — el estándar de la industria
— y correrá con `dotnet test` en cualquier máquina con acceso normal a NuGet, y en el pipeline
de CI/CD incluido. El sandbox donde se generó este repo no tenía salida a `nuget.org`, así que
la lógica que estas pruebas cubren fue verificada de otra forma: compilando `src/` completo
(sin dependencias NuGet externas, ver `AI-JOURNAL.md`) y ejecutando el servicio real contra los
mismos 5 escenarios que las pruebas de integración automatizan, vía `curl` HTTP directo.

## Estructura del repositorio

```
atlas-pars/
├── src/
│   ├── AtlasPars.Domain/          # Núcleo puro: modelos, motor de políticas. Cero dependencias.
│   ├── AtlasPars.Infrastructure/  # Adaptadores: firma HMAC, políticas en archivo, auditoría JSONL
│   └── AtlasPars.Api/             # Minimal API, orquestación, DTOs HTTP
├── tests/AtlasPars.Tests/         # xUnit: unitarias (motor, firma) + integración (endpoint real)
├── infra/                         # Terraform: Azure Container Apps + Key Vault
├── deploy/                        # Dockerfile (no-root) + docker-compose
├── .github/workflows/ci.yml       # Build+test+cobertura, SAST, SCA, escaneo Docker, IaC scan
├── policies/                      # Ejemplo de políticas por tenant (JSON)
├── docs/
│   ├── ARCHITECTURE.md            # C4 niveles 1-3
│   ├── ADR/0001-0004              # Decisiones de arquitectura con alternativas y trade-offs
│   ├── THREAT-MODEL.md            # STRIDE con 11 amenazas y mitigaciones
│   └── RUNBOOK.md                 # Rotación de llaves, rollback, troubleshooting
├── AI-JOURNAL.md                  # Uso de IA, honesto y específico
└── BACKLOG.md                     # Qué falta y por qué quedó fuera
```

## Cobertura de pruebas

El objetivo de cobertura (≥70%) aplica a la lógica de evaluación (`AtlasPars.Domain.Policies.
PolicyEvaluator`), no al agregado del repositorio — deliberadamente no se persigue cobertura alta
en DTOs, `Program.cs` (composición de dependencias) ni adaptadores triviales de infraestructura,
porque ahí la cobertura por sí sola no reduce riesgo real (ver rúbrica, "cobertura donde importa,
no en todo"). El pipeline de CI (`ci.yml`) calcula esta cobertura específicamente sobre
`AtlasPars.Domain` a partir del reporte Cobertura y **falla el build si es menor a 70%** (no solo
la reporta). El proyecto de tests incluye 10 pruebas unitarias del motor de políticas (deny-
overrides, default-deny, multi-tenant, todos los operadores del ejemplo, más una regresión de un
bug real — ver `AI-JOURNAL.md` sección 8) + 5 pruebas unitarias de la firma HMAC (integridad,
detección de tampering, determinismo — una de ellas es la regresión de un segundo bug crítico
real, también en la sección 8) + 6 pruebas de integración end-to-end contra la Api real vía
`WebApplicationFactory` (incluyendo la guardia de arranque fail-fast en producción).

**Nota importante de transparencia**: dos de estos tests habrían fallado con el código
originalmente entregado antes de esta ronda de verificación — no son pruebas triviales que
siempre pasan, sino regresiones reales de bugs encontrados ejecutando el código, documentados
en detalle en `AI-JOURNAL.md`.
