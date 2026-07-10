# Backlog priorizado — qué falta y por qué quedó fuera

Priorizado de mayor a menor impacto si este PoC avanzara a producción real.

## Resuelto en esta iteración (ver AI-JOURNAL.md y ADR-0004)

- ~~Check de arranque fail-fast si `Production` sin `ATLAS_SIGNING_KEY` explícita~~ — Implementado
  en `Program.cs` (ver THREAT-MODEL.md T-04).
- ~~OpenTelemetry realmente configurado~~ — SDK real con trazas + métricas + exporter (ver ADR-0004).
- ~~Rotación de llaves vía Key Vault~~ — `AzureKeyVaultKeyProvider` implementado y conectado al
  Key Vault que ya aprovisionaba `infra/main.tf` (antes provisto pero sin contraparte de código).
- ~~Gate de cobertura del pipeline no fallaba realmente~~ — `ci.yml` ahora calcula la cobertura
  específicamente de `AtlasPars.Domain` y falla el build si es menor a 70%.
- ~~Bug crítico: `Verify()` no ligaba la firma al contenido de la decisión~~ — corregido y con
  test de regresión (ver AI-JOURNAL.md, hallazgo de verificación offline).
- ~~Bug: crash si todas las políticas de un tenant están deshabilitadas~~ — corregido, ahora
  devuelve `Deny` fail-closed (ver THREAT-MODEL.md T-11).

## Pendiente, priorizado por impacto

1. **Autenticación del llamador (mTLS o OAuth2 client_credentials)** — Alto impacto de seguridad.
   Hoy `tenantId` se confía del body (ver THREAT-MODEL.md T-01). Es la siguiente pieza a construir
   antes de cualquier ambiente compartido real.
2. **Autorización del endpoint `GET /audit/{id}`** — Ver THREAT-MODEL.md T-08. Debe exigir que el
   llamador pertenezca al tenant de la decisión consultada.
3. **Persistencia de auditoría en Postgres (append-only real) o EventStoreDB** — El contrato
   `IAuditStore` ya está listo; falta el adaptador. Prioridad alta para producción por
   durabilidad y consultas más ricas que JSONL.
4. **Migración de firma HS256 a RS256/ES256** — Ver ADR-0002. Necesario antes de que auditores
   externos deban poder verificar decisiones sin poder falsificarlas (HMAC obliga a compartir la
   llave secreta con cada verificador).
5. **Rate limiting por tenant** — Ver THREAT-MODEL.md T-09.
6. **Validación de políticas en CI/CD antes de publicar** (rechazar `actions: ["*"]` sin
   condiciones) — Ver THREAT-MODEL.md T-10.
7. **Auditoría del propio evento de rotación de llaves** — Ver THREAT-MODEL.md T-06. Hoy la
   rotación en Key Vault queda en el log de actividad de Azure, pero no correlacionada con
   `AuditRecord` de Atlas PARS.
8. **UI/CLI de administración de políticas** — Fuera de alcance explícito de la prueba
   ("Frontend no se evalúa").
