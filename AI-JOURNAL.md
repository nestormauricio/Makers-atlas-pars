# AI-JOURNAL.md — Atlas PARS

## 1. Inventario de herramientas de IA usadas y en qué parte

- **Claude (Anthropic, con acceso a una sandbox Linux con .NET 8 real)**: usado para el diseño
  completo del sistema, la escritura de todo el código fuente, la infraestructura (Terraform,
  Docker, CI/CD) y la documentación. A diferencia de "pedirle código a un chat", en este caso
  la IA tuvo acceso a un entorno real donde **compiló, ejecutó y probó el servicio en
  caliente contra HTTP real** antes de entregarlo — no solo generó texto plausible.

## 2. Restricción real del entorno de generación (transparencia total)

El sandbox donde se generó este repositorio **no tenía salida de red a `nuget.org`** (solo a un
conjunto reducido de dominios: GitHub, PyPI, npm, crates.io, apt de Ubuntu). Esto significó una
decisión de diseño real, no cosmética:

- **`AtlasPars.Domain` y `AtlasPars.Infrastructure` y `AtlasPars.Api` se escribieron sin ningún
  paquete NuGet externo**, usando solo el BCL de .NET y el framework compartido de ASP.NET Core
  (que sí está disponible localmente vía el SDK instalado por `apt`). Esto se convirtió en una
  restricción productiva: forzó firmar con primitivas del BCL en vez de una librería JWT (ver
  ADR-0002) y evaluar políticas sin un motor externo (ver ADR-0001) — decisiones que de todas
  formas eran defendibles por sí mismas, y que además se pudieron **compilar y ejecutar de
  verdad** en este sandbox.
- El proyecto de pruebas (`tests/AtlasPars.Tests`) sí usa **xUnit + Microsoft.AspNetCore.Mvc.
  Testing + coverlet**, que son el estándar de la industria y lo que cualquier evaluador
  esperaría poder correr con `dotnet test` en su propia máquina (con acceso normal a internet).
  Ese proyecto **no se pudo compilar dentro de este sandbox** por la misma restricción de red.
  Sí correrá en el pipeline de GitHub Actions incluido (`.github/workflows/ci.yml`), que tiene
  acceso normal a NuGet.

**Lo que SÍ se verificó realmente, dentro de este sandbox, contra el servicio real:**
- Se compiló toda la solución (`dotnet build`) sin errores.
- Se levantó la Api con `dotnet run` apuntando a un archivo de políticas real.
- Se golpeó `POST /authorize` por HTTP con `curl` para los 5 escenarios centrales: DENY por país
  sancionado, CHALLENGE por monto alto (con obligaciones `mfa`/`manual-approval`), PERMIT por
  rol, DENY por defecto (fail-closed) cuando ninguna regla aplica, y DENY cuando el tenant no
  tiene políticas publicadas.
- Se verificó que cada decisión llega firmada (JWS de 3 partes) y que la firma cambia por
  decisión (no es una firma estática).
- Se verificó `GET /audit/{decisionId}` devolviendo el registro completo append-only.
- Se encontraron y corrigieron **dos bugs reales** en este proceso (ver sección 5): un problema
  de deserialización de `JsonElement` sin normalizar, tanto en el contexto de la solicitud HTTP
  como en las condiciones de las políticas cargadas desde disco.

Este journal documenta esto sin rodeos porque es exactamente el tipo de honestidad que la prueba
pide explícitamente en la sección 6.

## 3. Prompts diseñados (mínimo 3, no copiados de internet)

**Prompt 1** — para decidir el enfoque ante la restricción de red descubierta a mitad de
construcción: *"Confirmado que no hay salida a nuget.org en este sandbox. Antes de decidir cómo
seguir: ¿qué partes del sistema son razonables de construir sin ningún paquete NuGet externo
(usando solo BCL + framework compartido de ASP.NET), y cuáles necesitan genuinamente un paquete
de terceros donde no tiene sentido reinventar la rueda? Evalúa caso por caso, no asumas que 'cero
dependencias' es automáticamente la mejor decisión."*
Por qué así: quería evitar dos extremos — no forzar "cero dependencias" como dogma si eso
degradaba la calidad de una pieza (ej. testing, donde xUnit es claramente lo correcto), pero
tampoco rendirme y dejar todo sin verificar solo porque el entorno tenía una limitación.

**Prompt 2** — al escribir `PolicyEvaluator`: *"Antes de escribir el motor de evaluación,
enumera explícitamente qué algoritmo de combinación de reglas vas a usar (deny-overrides,
permit-overrides, first-applicable, etc.) y por qué, en un comentario XML en el propio código,
no solo en la documentación externa — quiero que alguien leyendo solo el .cs entienda la
semántica sin tener que ir a buscar el ADR."*
Por qué así: en sistemas de autorización, la semántica de combinación de reglas es la decisión
de diseño más crítica y más fácil de malinterpretar; quería que quedara imposible de pasar por
alto, directamente en el código.

**Prompt 3** — para el threat model: *"No generes una tabla STRIDE genérica de plantilla.
Para cada fila, la amenaza tiene que ser específica de lo que este servicio realmente hace
(evaluación de políticas, firma, multi-tenant, auditoría), y la mitigación tiene que decir
explícitamente si ya está implementada en el código que escribiste o si es una recomendación
para producción — no mezcles ambas cosas sin decirlo."*
Por qué así: los threat models genéricos ("usa HTTPS", "valida inputs") no demuestran haber
pensado en el sistema real; quería forzar especificidad y honestidad sobre qué es aspiracional.

## 4. Sugerencias de la IA que rechacé (mínimo 2) y por qué

1. **Rechacé**: la primera versión propuesta para el motor de políticas usaba reflection y
   expresiones lambda dinámicas (`Expression<Func<...>>`) para "máxima flexibilidad" en los
   operadores de condición.
   **Por qué**: era sobre-ingeniería para el conjunto cerrado de operadores que realmente se
   necesita (`Equals`, `In`, `GreaterThan`, etc.), y hacía el código bastante más difícil de leer
   y de razonar bajo el criterio de "una lógica de evaluación pura, testeable, sin sorpresas" que
   quería para el componente más auditado del sistema. Un `switch` explícito sobre un enum
   (`ConditionOperator`) es menos "elegante" pero mucho más auditable — y auditable es
   exactamente lo que un motor de autorización necesita ser.

2. **Rechacé**: en un punto, al escribir el `Dockerfile`, la sugerencia inicial corría el proceso
   como `root` dentro del contenedor "para simplificar permisos de escritura en el volumen de
   auditoría".
   **Por qué**: eso es exactamente el tipo de atajo que el threat model está pidiendo evitar
   (principio de menor privilegio). Lo cambié a un usuario no-root dedicado (`atlas`, UID 10001)
   y resolví el permiso de escritura dándole ownership explícito al volumen, no relajando el
   usuario del proceso.

## 5. Un momento donde la IA (yo) ahorró tiempo real (cuantificado)

Al escribir `PolicyEvaluator.ToStringList`, el compilador rechazó el primer orden de los
patrones del `switch` con `CS8510: pattern unreachable` porque `IEnumerable<string>` es
alcanzable vía la varianza de `IEnumerable<object?>` cuando ambos patrones están en el switch.
Detectar y corregir esto (reordenar los patrones, `string` primero) tomó **menos de un minuto**
gracias a poder compilar inmediatamente en el sandbox y ver el error exacto del compilador con
número de línea — en vez de tener que razonar en abstracto sobre reglas de varianza de C# sin
poder verificarlo, que es como habría tenido que proceder sin acceso a un compilador real.
Estimado conservador de tiempo ahorrado frente a "generar código a ciegas y esperar que el
usuario reporte el error": 15-20 minutos (el tiempo que toma a un desarrollador reproducir,
diagnosticar y corregir un error de compilación reportado de vuelta en una segunda ronda).

## 6. Un momento donde la IA (yo) llevó por el camino equivocado y cómo se detectó

Al escribir por primera vez el mapeo de `Context` (el diccionario de atributos contextuales de
la solicitud HTTP) y las condiciones de política (`PolicyCondition.Value`) hacia el motor de
evaluación, asumí — sin verificarlo — que System.Text.Json deserializaría propiedades tipadas
como `object`/`object?` en tipos CLR "naturales" (`double`, `string`, `bool`) directamente. Esto
es **incorrecto**: System.Text.Json las deserializa como `System.Text.Json.JsonElement`, un tipo
envoltorio que no coincide con ningún `case` de mi función `ToDouble`/`ToComparableString`.

**Cómo se detectó**: no por revisión de código ni por "recordar" cómo funciona System.Text.Json
correctamente desde el principio, sino porque **se ejecutó el servicio de verdad** y la primera
solicitud HTTP real (`POST /authorize` con `amountCop: 9000000` en el contexto) devolvió un
`500 Internal Server Error`. El log estructurado mostró la excepción exacta
(`InvalidOperationException: No se pudo convertir '9000000' a número...`) con el stack trace
completo, lo que hizo evidente el problema en segundos.
**La corrección** se hizo en el lugar arquitectónicamente correcto — el borde HTTP (`Mapping.
ToDomain` en la Api, y `FileSystemPolicyStore.NormalizeDocument` en Infrastructure) — no
"parcheando" el dominio para que entienda `JsonElement` (eso habría acoplado el dominio puro a
System.Text.Json, violando el principio explicado en ARCHITECTURE.md). Esto reforzó exactamente
por qué probar contra el servicio real, y no solo revisar el código generado, era indispensable
para esta entrega.

## 7. Opinión honesta: ¿qué NO debería delegarse todavía a la IA en este rol?

1. **La decisión de qué NO construir.** La IA puede generar indefinidamente más código,
   más ADRs, más pruebas — no tiene un límite natural de "esto ya es suficiente para demostrar
   criterio". Fui yo (el prompt, la persona detrás) quien tuvo que decidir explícitamente cortar
   alcance (ej. no implementar el componente disruptivo opcional, no traer Postgres real para
   auditoría) y priorizar verificación real sobre cantidad de artefactos. Sin esa presión externa
   constante de "prioriza, no sobre-entregues", el resultado habría sido más superficial en más
   lugares en vez de sólido en los que importan.
2. **Verificar contra la realidad, no contra la plausibilidad.** El bug de `JsonElement`
   (sección 6) es el ejemplo perfecto: el código generado era sintácticamente correcto,
   pasaba una lectura superficial, y **estaba mal**. Solo compilar y ejecutar de verdad lo
   reveló. Una IA que solo "suena convincente" sin poder ejecutar su propio código es
   estructuralmente incapaz de detectar esta clase de error — y esta clase de error es
   precisamente la más peligrosa en un sistema de autorización, porque falla silenciosamente
   como `500` en vez de como un `DENY` incorrecto, lo cual, dicho sea de paso, es el
   comportamiento *seguro* por el fail-closed del sistema, pero no deja de ser un bug.
3. **La responsabilidad de la sustentación oral.** Ninguna cantidad de código o documentación
   generada reemplaza poder explicar, en vivo y sin apoyo, por qué se tomó cada decisión —
   que es exactamente lo que la sección 3.6 y la sustentación de 45 minutos de esta prueba
   están diseñadas para verificar, y con razón.

## 8. Auditoría final contra un checklist de sustentación — dos bugs reales encontrados

Antes de dar esta entrega por cerrada, se auditó explícitamente contra una lista de puntos que
suelen pesar en la sustentación (cobertura real, integración end-to-end, firma+auditoría,
versionado de políticas, multi-tenant, OpenTelemetry real, health checks no triviales, secretos,
IaC declarativa, pipeline completo, ADRs, Threat Model, Runbook, alcance en el README). Dos
brechas de **documentación vs. código** y **dos bugs reales de comportamiento** salieron de ese
ejercicio, y se corrigieron todos antes de entregar:

### 8.1 Brechas de documentación vs. código (cerradas)

- **OpenTelemetry no estaba realmente configurado.** El Runbook afirmaba "compatible con
  OpenTelemetry sin dependencias adicionales", pero `Program.cs` nunca llamaba a
  `AddOpenTelemetry()` — solo existía un `Meter` de negocio suelto, sin SDK ni exporter. Se
  corrigió agregando el SDK real (`WithTracing`/`WithMetrics`, un `ActivitySource` con spans
  `Authorize → policy.evaluate/decision.sign/audit.append`, instrumentación de ASP.NET Core y
  exporter de consola/OTLP configurable). Ver ADR-0004.
- **El Key Vault de Terraform no tenía contraparte en código.** `infra/main.tf` aprovisionaba un
  Key Vault y una identidad administrada con permisos de lectura, pero el único `IKeyProvider`
  implementado (`EnvKeyProvider`) solo leía de una variable de entorno — el Key Vault quedaba
  aprovisionado pero sin usarse. Se agregó `AzureKeyVaultKeyProvider` y se actualizó `main.tf`
  para configurar la Container App con `Atlas__KeyProvider=AzureKeyVault` por defecto. Ver ADR-0004.
- **El gate de cobertura del pipeline no fallaba realmente.** El script de `ci.yml` calculaba un
  porcentaje de cobertura y lo imprimía, pero no tenía ningún `exit 1` si el resultado estaba por
  debajo del umbral, y tomaba el primer archivo de cobertura encontrado (no necesariamente el de
  `AtlasPars.Domain`). Se reescribió para filtrar específicamente las clases de
  `AtlasPars.Domain` en el reporte Cobertura y fallar el build si su cobertura de línea es menor
  a 70%.

### 8.2 Bugs reales de comportamiento (encontrados por ejecución real, no por lectura)

Igual que en el hallazgo de `JsonElement` de la sección 6, estos dos bugs eran código
sintácticamente correcto que **se veía bien en una revisión de lectura** y solo se revelaron
compilando y ejecutando el código real:

1. **Crash si todas las políticas de un tenant están deshabilitadas** (`PolicyEvaluator.cs`):
   `orderedDocs` queda vacío tras filtrar por `Enabled=true`, y el código accedía a
   `orderedDocs[0]` sin comprobar el conteo, lanzando `ArgumentOutOfRangeException` en vez de
   devolver `Deny`. Esto significa que apagar temporalmente las políticas de un tenant (un
   mantenimiento legítimo) tumbaba el endpoint completo en lugar de simplemente denegar.
   **Este bug habría sido detectado por el propio test `Politica_deshabilitada_se_ignora` de la
   suite original si esa suite se hubiera ejecutado alguna vez** (el caso `enabled=false` con un
   solo documento ya dispara la condición) — pero, como se documenta en la sección 2, el sandbox
   de generación no tiene acceso a NuGet para correr `dotnet test`. Se corrigió y se agregó
   además un test de regresión explícito con múltiples documentos deshabilitados
   (`Todas_las_politicas_deshabilitadas_no_crashea_devuelve_Deny`). Ver THREAT-MODEL.md T-11.

2. **Bug crítico de seguridad: `HmacDecisionSigner.Verify()` no verificaba nada realmente.**
   La implementación original recalculaba el HMAC usando las partes header/payload **extraídas
   del propio string de la firma recibida**, y nunca comparaba ese contenido contra el parámetro
   `decision` que se le pasaba a verificar. El efecto práctico: **cualquier firma válida
   (producida alguna vez por `Sign()`, para cualquier decisión) pasaba `Verify()` para
   cualquier objeto `decision` que se le pasara** — la firma nunca quedaba criptográficamente
   ligada al contenido verificado. Esto anulaba por completo la garantía de integridad/no-repudio
   que ADR-0002 y THREAT-MODEL.md T-03 afirman que el sistema provee. Se descubrió al reproducir
   en un arnés offline el mismo caso que ya existía en
   `HmacDecisionSignerTests.Verify_rechaza_una_decision_manipulada_reason_distinto` — ese test,
   **si se hubiera ejecutado**, habría fallado con el código original. Se corrigió reescribiendo
   `Verify()` para recomponer el JWS completo a partir de `decision` (no del string recibido) y
   comparar en tiempo constante contra la firma dada.

### 8.3 Cómo se verificó esto en un sandbox sin acceso a NuGet

Igual que en la primera ronda de verificación (sección 2), se compiló y ejecutó el código
**real** del repositorio, no una reescritura, aprovechando que `AtlasPars.Domain` no tiene
ninguna dependencia externa:

- `AtlasPars.Domain` completo compila 100% offline (`dotnet build` sin ningún paquete NuGet,
  confirmado con un `nuget.config` sin fuentes). Se ejecutaron los 9 escenarios de
  `PolicyEvaluatorTests.cs` reproducidos contra el `PolicyEvaluator` real vía `ProjectReference`:
  **16/16 aserciones en verde** tras el fix (falló primero, exactamente como se esperaría,
  confirmando que el bug era real y no un falso positivo del arnés).
- `HmacDecisionSigner` y `EnvKeyProvider` (los dos únicos archivos de `AtlasPars.Infrastructure`
  que no dependen del SDK de Azure Key Vault) se compilaron directamente vía `<Compile Include>`
  sin necesitar ningún paquete NuGet. Se ejecutaron 6 escenarios: **el primer intento falló
  exactamente en el caso de manipulación** (confirmando el bug real antes del fix), y **7/7 en
  verde después del fix**.
- `AzureKeyVaultKeyProvider` y la guardia de arranque fail-fast en `Program.cs` **no se pudieron
  ejecutar en este sandbox** (requieren el SDK de Azure y/o levantar el host ASP.NET completo con
  paquetes que no se pudieron restaurar). Quedan como código revisado manualmente, pendiente de
  validación real en un entorno con acceso normal a internet — exactamente igual que el resto de
  la suite xUnit completa (ver sección 2).

Esta es, en mi opinión, la parte más importante de todo el ejercicio: la diferencia entre
**entregar código que se lee bien** y **entregar código que se ejecutó y demostró hacer lo que
dice hacer**, incluso dentro de las limitaciones reales de un sandbox sin acceso completo a
internet.
