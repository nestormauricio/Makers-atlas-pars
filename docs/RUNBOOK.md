# Runbook — Atlas PARS

## Levantar el servicio

```bash
./run.sh
# equivalente manual:
cp .env.example .env && export $(grep -v '^#' .env | xargs)
docker compose -f deploy/docker-compose.yml up --build
```
Sin Docker: `dotnet run --project src/AtlasPars.Api` (requiere `ATLAS_SIGNING_KEY` en el entorno,
ver README.md "Cómo correrlo sin Docker").

## Ver logs

```bash
docker compose -f deploy/docker-compose.yml logs -f atlas-pars-api
```
Los logs son JSON estructurado (uno por línea) con `TraceId`/`SpanId` incluidos en cada entrada
(ver sección "Observabilidad" más abajo) — filtrables con `jq`, ej.:
```bash
docker compose -f deploy/docker-compose.yml logs atlas-pars-api | grep '"LogLevel":"Error"'
```

## Verificar health

```bash
curl -s http://localhost:8080/health | jq
```
`Healthy` implica: políticas legibles, audit store escribible y el firmante efectivamente firma/
verifica un payload sintético. Si algún componente falla, el JSON de respuesta indica cuál
(`policies_directory`, `audit_store_writable` o `signing_service`) y por qué.

## Rotación de llaves de firma

**Cuándo**: rotación programada (ej. cada 90 días) o rotación de emergencia (sospecha de
compromiso de la llave activa).

**Cómo (con `AzureKeyVaultKeyProvider`, `Atlas:KeyProvider=AzureKeyVault` — ver ADR-0004)**:
1. Generar una nueva versión del secreto en Key Vault:
   ```bash
   az keyvault secret set --vault-name <kv-name> --name atlas-signing-key \
     --value "$(openssl rand -base64 32)"
   ```
2. No hace falta desplegar nada: `AzureKeyVaultKeyProvider.GetActiveSigningKey()` cachea la
   llave activa solo 5 minutos, así que la nueva versión se vuelve activa sola. El `KeyId` de las
   decisiones nuevas es la versión del secreto en Key Vault (trazabilidad de rotación nativa).
3. `TryGetKey(keyId)` recupera versiones históricas específicas de Key Vault bajo demanda, así que
   `Verify()` sigue funcionando sobre decisiones firmadas con la llave anterior sin ningún cambio
   de código ni redeploy.
4. Confirmar en métricas/logs (`atlas_pars_decisions_total`, traza `decision.sign` con tag
   `atlas.key_id`) que las nuevas decisiones llevan el nuevo `KeyId`.
5. Tras el período de retención de auditoría (ver política de compliance del squad), purgar la
   versión anterior del secreto en Key Vault si corresponde.
6. Registrar el evento de rotación (quién, cuándo, por qué) — ver THREAT-MODEL.md T-06.

**Con `EnvKeyProvider` (`Atlas:KeyProvider=EnvVar`, solo dev/PoC)**: el mismo principio aplica
manualmente vía `ATLAS_SIGNING_KEY`/`ATLAS_SIGNING_KEY_ID` y un redeploy — ver el propio código de
`EnvKeyProvider` para el formato esperado. **Nunca usar este proveedor en producción**: el
arranque del proceso lo bloquea automáticamente si `ASPNETCORE_ENVIRONMENT=Production` sin una
llave explícita (ver ADR-0004, guardia fail-fast en `Program.cs`).

**Rotación de emergencia (llave comprometida)**: mismos pasos, pero saltando el período de
convivencia — se acepta que decisiones históricas firmadas con la llave comprometida ya no sean
re-verificables con la nueva llave únicamente (se documenta el incidente y se conserva la llave
comprometida solo para fines forenses de re-verificación retroactiva, nunca para firmar de nuevo).

## Cómo revertir un despliegue

Con Container Apps (`revision_mode = "Single"` en `infra/main.tf`), cada despliegue crea una
nueva revisión:
```bash
az containerapp revision list -n ca-atlas-pars-prod -g rg-atlas-pars-prod -o table
az containerapp ingress traffic set -n ca-atlas-pars-prod -g rg-atlas-pars-prod \
  --revision-weight <revision-anterior>=100
```
No se requiere rollback de datos: las políticas son versionadas e inmutables una vez publicadas
(un nuevo release de política es una nueva `version`, nunca una edición in-place), y la
auditoría es append-only, así que un rollback del servicio nunca corrompe auditoría ya escrita.

## Cómo investigar una decisión "DENY" sospechosa

1. Obtener el `decisionId` (viene en la respuesta del cliente, o búscalo en logs por
   `TenantId` + rango de tiempo).
2. `GET /audit/{decisionId}` → devuelve la solicitud completa (`Request`) y la decisión firmada.
3. Verificar la firma localmente (`IDecisionSigner.Verify`) para confirmar que el registro no
   fue alterado desde que se emitió.
4. Revisar `Decision.Reason`: si empieza con `rule:`, identifica la regla exacta (`RuleId`) que
   decidió — ir al documento de política de ese tenant (`policies/{tenantId}.json`) y a su
   `PolicyVersion` específica (las políticas están versionadas, así que si ya se publicó una
   nueva versión, aún puedes reconstruir exactamente qué regla estaba vigente cuando se tomó
   la decisión).
5. Si el `Reason` es `no-policy-defined` o `default-effect`, el tenant no tiene una regla
   explícita que cubra ese caso — probablemente falta una política, no es un bug del motor.

## Troubleshooting común

| Síntoma | Causa probable | Acción |
|---|---|---|
| `/health` devuelve `Unhealthy` | El directorio de políticas no existe o no es legible | Verificar el volumen/mount de `Atlas__PoliciesDirectory` |
| Todas las decisiones son `Deny` / `no-policy-defined` para un tenant | Falta el archivo `policies/{tenantId}.json`, o `enabled: false` en el documento | Publicar/corregir el documento de política del tenant |
| Latencia P95 sube por encima de 150ms | Caché de políticas invalidándose constantemente (archivo tocado en cada request) o volumen de auditoría con I/O lento | Revisar métrica `atlas_pars_authorize_latency_ms`; en producción, migrar auditoría a Postgres con connection pooling |
| Firma inválida al verificar una decisión antigua | La llave fue rotada y retirada antes de que expirara la ventana de verificación necesaria | Ver "Rotación de llaves" — conservar llaves retiradas el tiempo que dicte la política de retención de auditoría |

## Observabilidad

- **Logs estructurados**: JSON por línea vía `ILogger` (ver `Program.cs`, `AddJsonConsole` con
  `IncludeScopes=true`, lo que incluye `TraceId`/`SpanId` del `Activity` actual en cada línea,
  permitiendo correlacionar logs con trazas).
- **Trazas distribuidas (OpenTelemetry SDK real, ver ADR-0004)**: `Program.cs` registra
  `AddOpenTelemetry().WithTracing(...)` con el `ActivitySource "AtlasPars.Authorization"`. Cada
  llamada a `/authorize` genera un span `Authorize` con sub-spans `policy.evaluate`,
  `decision.sign`, `audit.append`, etiquetados con `atlas.tenant_id`, `atlas.effect`,
  `atlas.decision_id`, `atlas.key_id`. Exporta a consola por defecto; configurar
  `Atlas:OtlpEndpoint` (o las variables estándar `OTEL_EXPORTER_OTLP_*`) para enviar a un backend
  real (Jaeger, Tempo, Azure Monitor).
- **Métricas** (mismo SDK, `WithMetrics(...)` sobre el `Meter "AtlasPars.Authorization"`):
  - `atlas_pars_decisions_total{effect,tenant}` — contador.
  - `atlas_pars_authorize_latency_ms` — histograma, para vigilar el SLA de P95 < 150ms.
  - Instrumentación automática de ASP.NET Core (duración de requests HTTP, códigos de estado).
- **Health check compuesto (no trivial)**: `/health` valida tres componentes independientes:
  el directorio de políticas (`policies_directory`), que el audit store acepte escrituras
  (`audit_store_writable`), y que el firmante efectivamente firme y verifique un payload
  sintético (`signing_service`) — detecta una llave corrupta o ausente en el chequeo de salud,
  no en la primera decisión real de un tenant.
