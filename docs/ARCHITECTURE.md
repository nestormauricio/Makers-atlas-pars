# Arquitectura — Atlas PARS (Policy Access to Sensitive Resources)

## Nivel 1 — Contexto

```mermaid
C4Context
    title Atlas PARS — Diagrama de Contexto

    Person(dev, "Desarrollador de un squad", "Integra su servicio con Atlas PARS")
    System(squadSvc, "Servicio de un squad", "Ej: servicio de transferencias, servicio de cambio de datos personales")
    System(atlas, "Atlas PARS", "Evalúa políticas y emite decisiones PERMIT/DENY/CHALLENGE firmadas")
    System_Ext(keyvault, "Azure Key Vault", "Custodia las llaves de firma")
    System_Ext(gitops, "Repositorio Git de políticas", "Fuente de verdad versionada de las políticas por tenant")

    Rel(dev, squadSvc, "Desarrolla")
    Rel(squadSvc, atlas, "POST /authorize", "HTTPS/JSON")
    Rel(atlas, keyvault, "Obtiene llave activa de firma", "Managed Identity")
    Rel(gitops, atlas, "Publica políticas versionadas", "CI/CD o pull periódico")
```

Atlas PARS es un servicio de plataforma: cualquier squad lo consume para autorizar operaciones
críticas sin reimplementar lógica ABAC, firma criptográfica de decisiones o auditoría.

## Nivel 2 — Contenedores

```mermaid
C4Container
    title Atlas PARS — Diagrama de Contenedores

    Person(client, "Servicio cliente (squad)")

    Container_Boundary(atlas, "Atlas PARS") {
        Container(api, "Atlas.Api", ".NET 8 Minimal API", "Expone POST /authorize, /audit/{id}, /health")
        Container(engine, "Motor de políticas", ".NET 8, puro", "Evalúa ABAC con algoritmo deny-overrides")
        Container(signer, "Firmante de decisiones", ".NET 8 + BCL crypto", "Firma JWS (HMAC-SHA256) cada decisión")
        ContainerDb(policies, "Almacén de políticas", "JSON versionado por tenant", "Un documento por squad, cacheado en memoria")
        ContainerDb(audit, "Almacén de auditoría", "Append-only (JSONL / Postgres en prod)", "Nunca se actualiza ni borra")
    }

    System_Ext(keyvault, "Azure Key Vault")

    Rel(client, api, "HTTPS/JSON")
    Rel(api, engine, "Evalúa solicitud")
    Rel(api, signer, "Firma decisión")
    Rel(api, policies, "Lee políticas activas del tenant")
    Rel(api, audit, "Escribe registro de auditoría")
    Rel(signer, keyvault, "Obtiene llave activa", "en producción")
```

## Nivel 3 — Componentes (Api)

```mermaid
C4Component
    title Atlas.Api — Componentes

    Container_Boundary(api, "AtlasPars.Api") {
        Component(endpoint, "POST /authorize", "Minimal API endpoint", "Valida entrada, delega al orquestador")
        Component(orchestrator, "AuthorizationService", "Servicio de aplicación", "Evalúa -> firma -> audita -> responde")
        Component(mapping, "Mapping / normalización", "Traduce DTO HTTP <-> dominio, normaliza JsonElement")
    }

    Container_Boundary(domain, "AtlasPars.Domain (sin dependencias)") {
        Component(evaluator, "PolicyEvaluator", "Puro, determinista", "deny-overrides")
        Component(ports, "Puertos", "IPolicyStore / IAuditStore / IDecisionSigner")
    }

    Container_Boundary(infra, "AtlasPars.Infrastructure") {
        Component(store, "FileSystemPolicyStore", "Adaptador de IPolicyStore")
        Component(auditImpl, "JsonlAuditStore", "Adaptador de IAuditStore, append-only")
        Component(signerImpl, "HmacDecisionSigner", "Adaptador de IDecisionSigner, JWS/HS256")
    }

    Rel(endpoint, mapping, "usa")
    Rel(endpoint, orchestrator, "usa")
    Rel(orchestrator, evaluator, "usa")
    Rel(orchestrator, ports, "usa (inyectado)")
    Rel(store, ports, "implementa")
    Rel(auditImpl, ports, "implementa")
    Rel(signerImpl, ports, "implementa")
```

## Flujo de autorización (secuencia)

```mermaid
sequenceDiagram
    participant C as Cliente (servicio de negocio)
    participant A as AtlasPars.Api (/authorize)
    participant P as IPolicyStore
    participant E as PolicyEvaluator
    participant S as IDecisionSigner
    participant Au as IAuditStore

    C->>A: POST /authorize {tenantId, actor, resource, action, context}
    A->>P: GetActivePoliciesAsync(tenantId)
    P-->>A: políticas versionadas del tenant (o "*")
    A->>E: Evaluate(request, policies)
    E-->>A: Effect (Permit/Deny/Challenge) + policyId + reason
    A->>S: Sign(decision)
    S-->>A: (signature, keyId)
    A->>Au: AppendAsync(request, decision)  (append-only, JSONL)
    Au-->>A: ok
    A-->>C: 200 { effect, reason, policyId, signature, keyId }
```

Puntos clave del flujo: la decisión se firma **antes** de auditar y responder (para que la firma
cubra exactamente lo que se persiste y se devuelve), y el aislamiento de tenant se aplica dos
veces — al pedir las políticas (`GetActivePoliciesAsync(tenantId)`) y de nuevo como guard
defensivo dentro de `AuthorizationService` (ver ADR-0003).

## Por qué arquitectura hexagonal (puertos y adaptadores)

El dominio (`AtlasPars.Domain`) no tiene **ninguna** dependencia externa — ni de ASP.NET, ni de
System.Text.Json a nivel de tipos, ni de ningún proveedor de nube. Esto es deliberado:

1. **Testeable sin infraestructura**: `PolicyEvaluator` es una función pura (mismo input, mismo
   output, sin I/O), lo que permite testear miles de combinaciones de políticas en milisegundos.
2. **Reemplazable**: cambiar de archivos JSON a Postgres para políticas, o de HMAC a RS256 para
   firma, no toca una sola línea del dominio — solo se escribe un nuevo adaptador.
3. **Verificable bajo carga concurrente**: al no haber estado mutable compartido en el motor de
   evaluación, razonar sobre concurrencia (requisito: alta concurrencia, P95 < 150ms) es mucho
   más simple.

## Qué NO se construyó (alcance recortado, ver también BACKLOG.md)

- No hay una capa "Application" separada de la Api: para un solo caso de uso real, esa
  indirección no aportaba valor en el tiempo disponible (ver ADR-0001).
- No hay motor Rego/OPA embebido: se implementó un DSL JSON propio, más simple de razonar para
  este alcance (ver ADR-0001).
- Persistencia de auditoría es JSONL append-only en disco, no Postgres/EventStoreDB real (ver
  ADR-0003) — el contrato `IAuditStore` sí está listo para ese reemplazo.
- No se implementó UI de administración de políticas (fuera de alcance, "Frontend no se evalúa").
