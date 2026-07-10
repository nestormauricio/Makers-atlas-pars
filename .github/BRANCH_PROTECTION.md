# Política de aprobación / branch protection (documentada, ver sección 3.4)

Configuración esperada en GitHub para la rama `main` (se aplica vía UI o Terraform del
repositorio, no vía clics manuales recurrentes — ver `infra/` si se decide gestionar esto
como código con el provider `github`):

- Mínimo 1 aprobación de un CODEOWNER antes de mergear.
- Checks obligatorios antes de mergear: `build-test`, `sast`, `sca`, `docker-scan`, `iac-scan`.
- No se permite push directo a `main` (solo vía PR).
- No se permite force-push ni borrado de `main`.
- Firma de commits recomendada (no obligatoria en el PoC) para trazabilidad de autoría.
