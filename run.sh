#!/usr/bin/env bash
# Despliegue local con un solo comando (ver sección 3.3 de la prueba técnica).
set -euo pipefail
cd "$(dirname "$0")"

if [ ! -f .env ]; then
  echo "No existe .env — copiando desde .env.example (llave de desarrollo, no usar en prod)."
  cp .env.example .env
fi

export $(grep -v '^#' .env | xargs)
docker compose -f deploy/docker-compose.yml up --build
