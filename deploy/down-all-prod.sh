#!/usr/bin/env bash
# Derruba TUDO de produção (Caddy + Android + 10 stacks + ledger). Preserva os
# volumes (dados do Postgres, sessões WAHA, estado do Android). Rode da raiz:
#   bash deploy/down-all-prod.sh
set -euo pipefail
cd "$(dirname "$0")/.."

ENV_FILE=deploy/.env.prod
EF=()
[ -f "$ENV_FILE" ] && EF=(--env-file "$ENV_FILE")

COMPOSES=(docker-compose.yml docker-compose-2.yml docker-compose-3.yml docker-compose-4.yml \
          docker-compose-5.yml docker-compose-6.yml docker-compose-7.yml docker-compose-8.yml \
          docker-compose-9.yml docker-compose-10.yml)

echo "== Caddy =="
docker compose -f deploy/docker-compose.caddy.yml down || true

for f in "${COMPOSES[@]}"; do
  echo "== ${f} (inclui Android do profile phone) =="
  docker compose "${EF[@]}" -f "$f" --profile phone down || true
done

echo "== ledger compartilhado =="
docker compose "${EF[@]}" -f docker-compose.shared.yml down || true

echo "✓ Derrubado (volumes preservados). Pra apagar dados: adicione -v a cada 'down'."
