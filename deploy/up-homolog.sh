#!/usr/bin/env bash
# Sobe o ambiente de HOMOLOGAÇÃO (staging) — 1 stack ISOLADO, WAHA REAL, ledger Off, em
# https://hml.<domínio>. NÃO toca nos 10 stacks de prod (projeto/volumes/portas próprios).
# Rode da RAIZ do repo:  bash deploy/up-homolog.sh
#
# OBS: este script NÃO mexe no Caddy. O bloco hml.<domínio> vem do deploy/gen-config.sh; publique-o
# à parte (com `caddy validate` antes do reload) pra não arriscar o HTTPS dos 10.
set -euo pipefail
cd "$(dirname "$0")/.."   # raiz do repo

ENV_FILE=deploy/.env.homolog
[ -f "$ENV_FILE" ] || { echo "ERRO: $ENV_FILE não existe. Copie deploy/.env.homolog.example → deploy/.env.homolog e preencha."; exit 1; }

# Lê MTRX_DOMAIN sem expandir (hash bcrypt tem '$'). `|| true` p/ não abortar sob set -e num grep vazio.
getenv() { grep -E "^$1=" "$ENV_FILE" | head -1 | cut -d= -f2- || true; }
MTRX_DOMAIN=$(getenv MTRX_DOMAIN); MTRX_DOMAIN=${MTRX_DOMAIN:-mtrxsys.com}

# Guard mínimo de segredo: não subir homolog com o placeholder do template.
if grep -q '__TROQUE__' "$ENV_FILE" || grep -q '__TROQUE_32_CHARS_NO_MINIMO__' "$ENV_FILE"; then
  echo "ERRO: ainda há __TROQUE__ no $ENV_FILE — preencha os segredos antes de subir."
  exit 1
fi

# URLs públicas (mesma origem via Caddy) — o deploy/docker-compose.prod.yml usa pra o build do web.
export WEB_PUBLIC_API_URL="https://hml.${MTRX_DOMAIN}"
export WEB_EMULATOR_URL=""
export LANDING_ORIGIN="https://app.${MTRX_DOMAIN}"

echo "== Homolog: subindo o stack isolado mtrxhml (WAHA real, ledger Off) em https://hml.${MTRX_DOMAIN} =="
docker compose --env-file "$ENV_FILE" \
  -f docker-compose-hml.yml \
  -f deploy/docker-compose.prod.yml \
  up -d --build

cat <<EOF

✓ Homolog no ar (atrás do portão de auth):
  App:  https://hml.${MTRX_DOMAIN}   (login: SEEDH_ADMIN_*)
  WAHA: pareie o chip DESCARTÁVEL pela aba "Celular" (modo WahaOnly — sem emulador).

Se o hml.${MTRX_DOMAIN} ainda não abrir, publique o Caddy:
  bash deploy/gen-config.sh
  docker exec mtrx-caddy caddy validate --config /etc/caddy/Caddyfile   # valida ANTES
  docker exec mtrx-caddy caddy reload   --config /etc/caddy/Caddyfile   # recarrega sem derrubar
EOF
