# ADR-0004: Endurecimiento criptográfico (Key Vault real) y observabilidad real (OpenTelemetry SDK)

## Estado
Aceptado

## Contexto
La primera versión de esta entrega tenía dos brechas entre lo que la documentación describía y
lo que el código realmente hacía:

1. **Observabilidad**: `docs/RUNBOOK.md` afirmaba que las métricas eran "compatibles con
   OpenTelemetry sin dependencias adicionales", pero `Program.cs` nunca registraba el SDK de
   OpenTelemetry (`AddOpenTelemetry()`), ni había *exporter* ni trazas configuradas. Existía un
   `Meter`/`Counter`/`Histogram` de negocio, pero nada los recolectaba ni exportaba realmente.
2. **Rotación de llaves**: `infra/main.tf` aprovisiona un Key Vault y una identidad administrada
   con permisos de lectura, pero el único `IKeyProvider` implementado (`EnvKeyProvider`) solo lee
   de una variable de entorno — el Key Vault de Terraform no tenía ninguna contraparte real en
   el código de la aplicación.

## Decisión
1. Se agregó configuración real de OpenTelemetry (`OpenTelemetry.Extensions.Hosting`) con
   `WithTracing()` + `WithMetrics()`, un `ActivitySource` propio (`AuthorizationService
   .ActivitySourceName`) con spans `Authorize` → `policy.evaluate` / `decision.sign` /
   `audit.append`, instrumentación automática de ASP.NET Core, y un exporter de consola por
   defecto (con soporte para `Atlas:OtlpEndpoint` hacia un backend real).
2. Se agregó `AzureKeyVaultKeyProvider : IKeyProvider`, seleccionable vía
   `Atlas:KeyProvider=AzureKeyVault`, que obtiene la llave activa y llaves históricas desde Key
   Vault (cada *versión* del secreto es una llave rotada, dando trazabilidad de rotación nativa).
   `infra/main.tf` ahora configura la Container App para usar este proveedor por defecto.
3. Se agregó una **guardia de arranque fail-fast**: si `ASPNETCORE_ENVIRONMENT=Production` y
   `Atlas:KeyProvider=EnvVar` sin `ATLAS_SIGNING_KEY` explícita, el proceso **no arranca** (antes,
   arrancaba en silencio con la llave insegura de desarrollo — ver THREAT-MODEL.md T-04, ahora
   mitigado en vez de solo documentado como pendiente).
4. Se fortaleció el health check: además de verificar que el directorio de políticas exista,
   ahora también valida que el audit store acepte escrituras y que el firmante efectivamente
   firme y verifique un payload sintético (`SigningServiceHealthCheck`) — detecta una llave
   corrupta o ausente en el chequeo de salud, no en la primera decisión real.

## Consecuencias
- `AzureKeyVaultKeyProvider` no pudo compilarse/ejecutarse en el sandbox de generación (sin
  acceso a `nuget.org` para restaurar `Azure.Security.KeyVault.Secrets`/`Azure.Identity`, ver
  AI-JOURNAL.md). Se revisó manualmente contra la documentación pública del SDK y debe validarse
  con `dotnet build` + una prueba de integración real contra un Key Vault de desarrollo antes de
  activarlo en un despliegue real.
- El exporter de consola de OpenTelemetry es apto para desarrollo/demo; producción requiere
  configurar `Atlas:OtlpEndpoint` (o variables estándar `OTEL_EXPORTER_OTLP_*`) hacia un backend
  real (Azure Monitor, Tempo, Jaeger).
- La guardia de arranque fail-fast es una ruptura de compatibilidad intencional: un despliegue mal
  configurado que antes arrancaba (inseguro) ahora falla explícitamente. Esto es deseable (fail
  closed > fail open) pero debe comunicarse en el runbook de despliegue.
