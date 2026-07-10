# PITCH-SCRIPT.md — Guion para el video de 5 minutos (sección 3.6)

Dirigido a dos públicos en el mismo video: un líder de producto (sin trasfondo técnico) y un
arquitecto senior (que va a cuestionar). Marcado dónde cambia el registro.

## 0:00–0:45 — Qué es Atlas PARS (para el líder de producto)
> "Atlas PARS resuelve un problema que hoy cada squad resuelve mal y por separado: decidir si
> una operación sensible —una transferencia, un cambio de datos personales— se permite o no.
> En vez de que cada equipo reinvente esa lógica, la centralizamos en un servicio: cualquier
> squad le pregunta '¿puedo hacer esto?', y Atlas responde PERMIT, DENY o CHALLENGE, con una
> firma digital que prueba que esa decisión no se puede falsificar después."

## 0:45–1:30 — Por qué importa (para el líder de producto)
> "Esto reduce riesgo y tiempo de entrega a la vez: seguridad y compliance dejan de depender de
> que cada squad implemente bien su propia lógica de autorización, y los squads dejan de gastar
> semanas en eso. Cada decisión queda auditada — si un regulador pregunta 'por qué se permitió
> esta transferencia', tenemos la respuesta firmada y trazable, no un log que alguien pudo editar."

## 1:30–2:30 — Cómo funciona (transición al arquitecto)
> "Bajo el capó: es un servicio .NET 8, arquitectura hexagonal — el dominio, o sea la lógica de
> evaluación de políticas, no depende de nada externo, ni de ASP.NET ni de una base de datos.
> Eso significa que puedo testear miles de combinaciones de reglas en milisegundos, sin
> infraestructura. Las políticas son documentos JSON versionados por tenant, con un algoritmo
> deny-overrides explícito: si cualquier regla dice DENY, gana DENY, sin ambigüedad."

## 2:30–3:30 — Decisiones defendibles (para el arquitecto senior)
> "Tomé tres decisiones que documenté en ADRs y que defiendo activamente:
> Uno, no usé OPA/Rego — implementé un DSL propio porque para el volumen de reglas esperado,
> Rego era más complejidad de la que el problema pedía, y quería que cualquier ingeniero C#
> pudiera leer el motor de punta a punta sin aprender un lenguaje nuevo.
> Dos, la firma es JWS con HMAC-SHA256 construido a mano sobre primitivas del BCL — no inventé
> el algoritmo criptográfico, solo el envoltorio de serialización, para no traer una librería
> JWT completa a cambio de features que no uso todavía. Documenté explícitamente que antes de
> producción multi-tenant real esto migra a RS256 vía Key Vault, y ya dejé el adaptador
> (`AzureKeyVaultKeyProvider`) escrito y listo detrás del mismo puerto.
> Tres, encontré y corregí dos bugs reales verificando el sistema en caliente, no solo leyendo
> el código: un bug crítico en la verificación de firma que hacía que cualquier firma válida
> pasara para cualquier decisión, y un crash cuando todas las políticas de un tenant estaban
> deshabilitadas. Ambos quedaron cubiertos por pruebas específicas."

## 3:30–4:15 — Qué NO construí y por qué (para ambos públicos)
> "Deliberadamente no until construí: autenticación real del llamador — el servicio hoy confía
> en el tenantId del body, documentado como el gap de seguridad más importante a cerrar antes de
> producción, priorizado como el ítem número uno del backlog. Tampoco usé Postgres para
> auditoría, sino archivos append-only — el contrato de persistencia ya está listo para
> cambiarlo sin tocar el dominio. Prioricé verificar sólidamente lo obligatorio sobre construir
> el componente disruptivo opcional."

## 4:15–5:00 — Cierre (para ambos públicos)
> "En resumen: un núcleo pequeño, verificado de verdad —no solo generado—, con decisiones de
> arquitectura explícitas y defendibles, infraestructura como código real en Terraform, un
> pipeline que hace SAST, SCA, escaneo de imagen e IaC scanning, y total transparencia sobre
> qué falta y por qué. Eso es lo que estoy listo para defender en la sustentación."

---
**Notas de grabación** (no forman parte del guion hablado):
- Grabar con la terminal mostrando `./run.sh` levantando el servicio y un `curl` real a
  `/authorize` como apoyo visual durante el minuto 2:30–3:30.
- Mantener el tono conversacional del líder de producto en los primeros 90 segundos: cero
  jerga técnica (nada de "ABAC", "JWS", "deny-overrides" todavía).
- El cambio de registro hacia el arquitecto debe ser explícito y notorio, no gradual — es parte
  de lo que la prueba pide evaluar ("cómo modulas el lenguaje").
