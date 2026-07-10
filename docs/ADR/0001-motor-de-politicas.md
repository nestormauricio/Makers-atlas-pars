# ADR-0001: DSL JSON propio en vez de OPA/Rego completo para el motor de políticas

## Estado
Aceptado

## Contexto
El servicio necesita evaluar políticas declarativas estilo ABAC/OPA-like, versionadas, con
soporte para PERMIT/DENY/CHALLENGE. La opción "obvia" de la industria es embeber Open Policy
Agent (OPA) y escribir las políticas en Rego.

## Decisión
Implementamos un motor de evaluación propio en C# puro, con políticas expresadas como documentos
JSON estructurados (acciones, tipos de recurso, condiciones ABAC con operadores fijos), en vez de
integrar OPA/Rego.

## Alternativas consideradas

1. **Embeber OPA (motor Rego) vía SDK o sidecar.**
   - Pros: estándar de facto, Rego es expresivo, comunidad grande, ya resuelve deny-overrides y
     otras semánticas de combinación.
   - Contras: (a) trae una dependencia externa pesada (proceso o SDK) que en un PoC de 12-15h
     efectivas consume tiempo de integración que no se traduce en demostrar criterio propio;
     (b) Rego tiene una curva de aprendizaje no trivial para "cualquier squad" que declaraste
     como consumidor objetivo — un DSL JSON es más accesible para un desarrollador que no vive
     en políticas de seguridad; (c) para el volumen de reglas esperado (decenas por tenant, no
     miles), la expresividad completa de Rego es más capacidad de la que se necesita.

2. **DSL JSON propio (elegido).**
   - Pros: cero dependencias externas (se puede testear y razonar sin infraestructura), curva de
     adopción baja para un squad consumidor, control total sobre la semántica de combinación
     (deny-overrides explícito y auditable en ~150 líneas), evaluable en <1ms por ser puro C#
     sin serialización intermedia a un runtime externo (ayuda directamente al SLA de P95<150ms).
   - Contras: expresividad limitada (no hay recursión, no hay funciones custom, los operadores
     son un conjunto cerrado); si las reglas necesarias en el futuro se vuelven muy complejas
     (ej. políticas que dependen de resultados de otras políticas), este DSL se queda corto y
     habría que migrar a un motor más expresivo.

## Consecuencias
- Positivo: el core del servicio (dominio) no depende de ningún proceso externo ni SDK de
  terceros, lo que simplifica testing, despliegue y auditoría de seguridad (menos superficie de
  supply-chain).
- Positivo: cualquier ingeniero C# puede leer `PolicyEvaluator.cs` de punta a punta y entender
  exactamente cómo se combina una decisión, sin conocer un lenguaje de políticas adicional.
- Negativo: si Atlas crece y necesita expresividad tipo Rego (ej. políticas que consultan datos
  externos dentro de la misma evaluación, recursión sobre jerarquías de recursos), este DSL
  requerirá una reescritura o una migración a OPA. El puerto `IPolicyStore` y el contrato de
  `PolicyDocument` están diseñados para que ese reemplazo sea localizado (ver ARCHITECTURE.md).
- Se documenta explícitamente como decisión revisable: si en 6 meses el catálogo de políticas
  crece más allá de "reglas ABAC con condiciones simples", reabrir esta decisión.
