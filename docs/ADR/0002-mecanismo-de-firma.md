# ADR-0002: JWS compacto hecho a mano (HMAC-SHA256) en vez de una librería JWT completa

## Estado
Aceptado (revisable antes de producción multi-tenant real — ver "Consecuencias")

## Contexto
Cada decisión debe quedar firmada criptográficamente, con trazabilidad auditable. El estándar de
la industria para esto es JWS (RFC 7515), típicamente consumido a través de una librería como
`System.IdentityModel.Tokens.Jwt` o `jose-jwt`.

## Decisión
Implementamos a mano solo la serialización compacta de JWS (`base64url(header).base64url(payload).
base64url(HMACSHA256(header.payload))`), usando exclusivamente `System.Security.Cryptography.
HMACSHA256` del BCL de .NET — sin traer una librería JWT de terceros.

**Importante — qué NO estamos haciendo**: no estamos inventando un algoritmo criptográfico
("rolled-your-own crypto"). El algoritmo de firma es HMAC-SHA256, una primitiva estándar,
tal cual la implementa el BCL. Lo que escribimos a mano es únicamente el *envoltorio* de
serialización (cómo se concatenan y codifican header/payload/firma), que es mecánico y
directamente verificable contra el RFC.

## Alternativas consideradas

1. **Librería JWT completa (ej. `System.IdentityModel.Tokens.Jwt`).**
   - Pros: soporta el estándar JOSE completo (RS256, ES256, validación de claims estándar como
     `exp`/`nbf`, rotación de llaves vía JWKS).
   - Contras: para este PoC, el 90% de esa superficie no se usa (no hay `exp` en una decisión de
     autorización histórica, no hay JWKS todavía). Traer la librería completa es superficie de
     dependencia adicional en el componente más sensible del sistema, a cambio de features no
     usadas.

2. **JWS compacto a mano sobre BCL (elegido).**
   - Pros: ~80 líneas totales, 100% auditables en una sola lectura; cero dependencias de
     terceros en el componente de firma; permite compilar y correr el core del sistema sin
     acceso a un feed de paquetes (relevante para este entorno de generación, ver AI-JOURNAL.md).
   - Contras: solo soporta HS256 (llave simétrica compartida). Esto es una limitación real para
     producción multi-tenant: con HMAC, cualquiera que tenga la llave de firma puede *también*
     falsificar una firma válida (la misma llave firma y verifica). Para un ambiente donde
     distintos squads o auditores externos necesitan **verificar** una firma sin poder
     **producir** una firma válida, se necesita criptografía asimétrica (RS256/ES256), donde la
     llave pública se distribuye libremente y solo Atlas posee la llave privada.

## Consecuencias
- Positivo inmediato: cero dependencias externas en el firmante, compila y corre en cualquier
  entorno con solo el SDK de .NET.
- Negativo a mediano plazo: **antes de producción real**, este mecanismo debe migrar a RS256 o
  ES256 vía Azure Key Vault (que soporta operaciones de firma con llave asimétrica sin exponer
  la llave privada nunca fuera del HSM). El puerto `IDecisionSigner` ya está diseñado para que
  ese reemplazo sea un adaptador nuevo, sin tocar el dominio ni el orquestador.
- El `KeyId` (`kid`) ya viaja en cada decisión firmada, preparando el terreno para rotación de
  llaves y para JWKS cuando se migre a llaves asimétricas (ver RUNBOOK.md, "Rotación de llaves").
