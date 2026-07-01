#!/usr/bin/env bash
# Gera, a partir de MTRX_DOMAIN (em deploy/.env.prod):
#   • deploy/Caddyfile        — reverse proxy HTTPS dos 10 ambientes + landing + noVNC
#   • deploy/landing/         — cópia da landing com as URLs reescritas pra produção
# Idempotente: rode de novo sempre que mudar o domínio.
set -euo pipefail
cd "$(dirname "$0")"

[ -f .env.prod ] || { echo "ERRO: deploy/.env.prod não existe (rode gen-secrets.sh)."; exit 1; }
# Lê um valor LITERAL do .env.prod (sem expandir — o hash bcrypt tem '$').
getenv() { grep -E "^$1=" .env.prod | head -1 | cut -d= -f2-; }
MTRX_DOMAIN=$(getenv MTRX_DOMAIN)
MTRX_TLS_EMAIL=$(getenv MTRX_TLS_EMAIL)
MTRX_PHONE_BASIC_USER=$(getenv MTRX_PHONE_BASIC_USER)
MTRX_PHONE_BASIC_HASH=$(getenv MTRX_PHONE_BASIC_HASH)
[ -n "$MTRX_DOMAIN" ] || { echo "ERRO: defina MTRX_DOMAIN no deploy/.env.prod"; exit 1; }

LETTERS=(a b c d e f g h i j)
WEB_PORTS=(5173 5174 5176 5177 5178 5179 5180 5181 5182 5183)
API_PORTS=(5080 5081 5082 5083 5084 5085 5086 5087 5088 5089)
NOVNC_PORTS=(6080 6081 6082 6083 6084 6085 6086 6087 6088 6089)

# ── Caddyfile ────────────────────────────────────────────────────────────────
{
  echo "# GERADO por deploy/gen-config.sh — não edite à mão (rode o script de novo)."
  echo "{"
  echo "    email ${MTRX_TLS_EMAIL:-admin@${MTRX_DOMAIN}}"
  echo "}"
  echo
  echo "# Landing (seleção de ambiente)"
  echo "app.${MTRX_DOMAIN} {"
  echo "    root * /srv/landing"
  echo "    file_server"
  echo "}"
  for i in "${!LETTERS[@]}"; do
    L=${LETTERS[$i]}
    echo
    echo "# ── Ambiente ${L} (dashboard + api mesma-origem) ──"
    echo "${L}.${MTRX_DOMAIN} {"
    echo "    @api path /api/* /webhooks/* /health* /sair*"
    echo "    reverse_proxy @api 127.0.0.1:${API_PORTS[$i]}"
    echo "    reverse_proxy 127.0.0.1:${WEB_PORTS[$i]}"
    echo "}"
    echo "# Tela do Android (noVNC) — protegida por basic-auth"
    echo "phone-${L}.${MTRX_DOMAIN} {"
    if [ -n "${MTRX_PHONE_BASIC_HASH:-}" ]; then
      echo "    basic_auth {"
      echo "        ${MTRX_PHONE_BASIC_USER:-operador} ${MTRX_PHONE_BASIC_HASH}"
      echo "    }"
    else
      echo "    # AVISO: MTRX_PHONE_BASIC_HASH vazio → noVNC SEM senha. Rode gen-secrets.sh."
    fi
    echo "    reverse_proxy 127.0.0.1:${NOVNC_PORTS[$i]}"
    echo "}"
  done
} > Caddyfile
echo "✓ deploy/Caddyfile gerado."

# ── Landing reescrita pra produção ───────────────────────────────────────────
mkdir -p landing
cp ../landing/index.html landing/index.html
[ -f ../landing/favicon.svg ] && cp ../landing/favicon.svg landing/favicon.svg || true
for i in "${!LETTERS[@]}"; do
  L=${LETTERS[$i]}
  # backend (api) e target (web) do dev → o mesmo host público do ambiente.
  sed -i "s#http://localhost:${API_PORTS[$i]}#https://${L}.${MTRX_DOMAIN}#g" landing/index.html
  sed -i "s#http://localhost:${WEB_PORTS[$i]}#https://${L}.${MTRX_DOMAIN}#g" landing/index.html
done
echo "✓ deploy/landing/ gerado (URLs apontando pra https://<letra>.${MTRX_DOMAIN})."
