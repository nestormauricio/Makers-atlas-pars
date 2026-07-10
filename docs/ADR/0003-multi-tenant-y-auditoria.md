# ADR-0003: Multi-tenant por particionamiento lógico (no despliegue físico por squad) + auditoría append-only por archivo (PoC) con contrato listo para Postgres/EventStore

## Estado
Aceptado

## Contexto
El servicio debe ser "multi-tenant y desplegable de forma autónoma por cada squad". Hay que
decidir: (a) cómo se logra el aislamiento entre tenants, y (b) cómo se persiste la auditoría
append-only exigida por el reto.

## Decisión

### Multi-tenant: particionamiento lógico
Un único despliegue de Atlas PARS sirve a todos los tenants. El aislamiento se logra a nivel de
aplicación:
- Cada solicitud lleva `tenantId` explícito.
- `IPolicyStore.GetActivePoliciesAsync(tenantId, ...)` solo puede devolver políticas de ese
  tenant (un archivo/registro por tenant).
- `AuthorizationService` tiene un guard defensivo: si por bug el store devolviera políticas de
  otro tenant, lanza una excepción en vez de evaluar (fail-closed, ver THREAT-MODEL.md).
- La auditoría se particiona físicamente por archivo/tabla por tenant (un `.jsonl` por tenant en
  el PoC), de modo que un tenant nunca puede, ni por error de query, leer auditoría de otro.

### Auditoría: append-only por archivo en el PoC, contrato listo para BD real
Se implementó `JsonlAuditStore`: un archivo JSON Lines por tenant, abierto siempre en modo
`Append`, nunca se reescribe ni se borra una línea ya escrita.

## Alternativas consideradas (multi-tenant)

1. **Un despliegue (container/namespace) por squad.**
   - Pros: aislamiento físico fuerte, cada squad puede desplegar/actualizar su instancia de forma
     verdaderamente autónoma sin coordinar con otros.
   - Contras: multiplica el costo operativo (N despliegues, N sets de alertas, N rotaciones de
     llave) por cada squad que se suma; para el volumen esperado (política de acceso compartida,
     no un servicio de negocio por squad), es sobre-ingeniería.

2. **Particionamiento lógico en un despliegue compartido (elegido).**
   - Pros: un solo servicio que operar, escala y observa; el "desplegable de forma autónoma"
     se resuelve a nivel de *políticas* (cada squad publica sus políticas sin tocar el código del
     servicio ni coordinar un release) en vez de a nivel de infraestructura.
   - Contras: un bug de aislamiento en el servicio compartido afecta a todos los tenants a la vez
     (mitigado con el guard defensivo + partición física de auditoría + tests de aislamiento).

## Alternativas consideradas (auditoría)
1. **Postgres con tabla append-only real (sin permisos UPDATE/DELETE).**
   Descartado para el PoC por tiempo, pero es la recomendación explícita para producción.
2. **EventStoreDB / event sourcing real.**
   Es la opción más alineada con "time-travel audit" (ver componente disruptivo B de la prueba),
   pero trae infraestructura adicional que no se justifica para demostrar el contrato en 12-15h.
3. **JSONL append-only en disco (elegido para el PoC).**
   Cumple la garantía observable (append-only, particionado por tenant, consultable) sin traer
   una BD. El puerto `IAuditStore` no cambia si se reemplaza el adaptador por Postgres.

## Consecuencias
- El aislamiento multi-tenant depende de disciplina en el código (el guard defensivo) más que de
  una barrera física del sistema operativo — es una decisión consciente de costo/beneficio para
  este alcance, documentada como riesgo en THREAT-MODEL.md (T-02).
- Antes de producción con tenants de alto riesgo/compliance estricto, evaluar si algún squad
  específico necesita aislamiento físico (namespace o despliegue dedicado) — el diseño no lo
  impide, es una decisión por tenant, no arquitectónica global.
- La migración de `JsonlAuditStore` a Postgres es un adaptador nuevo detrás de `IAuditStore`,
  sin tocar dominio ni orquestador (mismo patrón que ADR-0001 y ADR-0002).
