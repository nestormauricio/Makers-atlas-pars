# ADR-0000: Uso de .NET 8 (LTS) como plataforma del servicio

## Estado
Aceptado

## Contexto
Atlas PARS necesita una plataforma para un servicio HTTP de baja latencia (P95 < 150ms objetivo),
con buen soporte de criptografía nativa (HMAC/SHA-256), tipado fuerte para modelar un dominio de
reglas (ABAC) sin errores de casteo en producción, y un ecosistema maduro de librerías cloud
(Azure SDK, OpenTelemetry).

## Decisión
Se construyó el servicio en **.NET 8 (LTS)** con ASP.NET Core Minimal APIs.

## Razones
- **LTS (soporte hasta noviembre de 2026)**: evita forzar una migración de runtime a mitad de la
  vida útil de un servicio de plataforma que otros equipos van a consumir.
- **Minimal APIs**: para el tamaño de este servicio (2 endpoints reales) evita la ceremonia de
  Controllers/MVC sin sacrificar testabilidad (`WebApplicationFactory<Program>` funciona igual).
- **Rendimiento**: Kestrel + Minimal APIs tiene overhead mínimo por request, relevante para el
  SLA de P95 < 150ms.
- **Tipado fuerte + `record` inmutables**: el modelo de dominio (`AuthorizationRequest`,
  `PolicyDocument`, `AuthorizationDecision`) se beneficia de `record` con igualdad estructural e
  inmutabilidad, reduciendo la clase de bugs "alguien mutó el objeto a mitad de la evaluación".
- **Criptografía nativa**: `System.Security.Cryptography.HMACSHA256` y
  `CryptographicOperations.FixedTimeEquals` (comparación en tiempo constante) están en el BCL, sin
  necesitar una librería criptográfica de terceros para el mecanismo de firma (ver ADR-0002).

## Consecuencias
- El equipo que mantenga este servicio necesita conocimiento de .NET/C#; no es una decisión
  "poliglota-neutral", pero es consistente con el resto del stack de la organización objetivo del
  ejercicio (equipos .NET, ver enunciado de la prueba).
- Minimal APIs es una elección deliberadamente distinta de Controllers/MVC: si el servicio
  creciera a decenas de endpoints, valdría la pena revisar esta decisión (no es el caso hoy, con
  2 endpoints reales).
