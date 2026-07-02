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
GATE_UP="127.0.0.1:8099"   # serviço-portão (deploy/docker-compose.gate.yml)
# snippet forward_auth: exige a sessão do portão; sem ela o /authz responde 302 → tela de login.
fauth() {
  echo "    forward_auth ${GATE_UP} {"
  echo "        uri /authz"
  echo "    }"
}
{
  echo "# GERADO por deploy/gen-config.sh — não edite à mão (rode o script de novo)."
  echo "{"
  echo "    email ${MTRX_TLS_EMAIL:-admin@${MTRX_DOMAIN}}"
  echo "}"
  echo
  echo "# Portão de login (usuário + senha + 2FA) — a PRÓPRIA tela de login, SEM forward_auth."
  echo "auth.${MTRX_DOMAIN} {"
  echo "    reverse_proxy ${GATE_UP}"
  echo "}"
  echo
  echo "# Landing (seleção de ambiente) — atrás do portão."
  echo "app.${MTRX_DOMAIN} {"
  fauth
  echo "    root * /srv/landing"
  echo "    file_server"
  echo "}"
  for i in "${!LETTERS[@]}"; do
    L=${LETTERS[$i]}
    echo
    echo "# ── Ambiente ${L} (dashboard + api mesma-origem) — atrás do portão + CORS p/ a landing ──"
    echo "${L}.${MTRX_DOMAIN} {"
    echo "    @preflight method OPTIONS"
    echo "    @api path /api/* /webhooks/* /health*"
    echo "    @optout path /sair*"
    echo "    # Preflight CORS: responde direto (sem portão), senão o forward_auth daria 302 no OPTIONS."
    echo "    handle @preflight {"
    echo "        header Access-Control-Allow-Origin \"https://app.${MTRX_DOMAIN}\""
    echo "        header Access-Control-Allow-Credentials \"true\""
    echo "        header Access-Control-Allow-Methods \"GET, POST, OPTIONS\""
    echo "        header Access-Control-Allow-Headers \"Content-Type\""
    echo "        header Access-Control-Max-Age \"600\""
    echo "        respond 204"
    echo "    }"
    echo "    # Opt-out PÚBLICO (LGPD): o link /sair das mensagens NÃO pode passar pelo portão — o"
    echo "    # destinatário não tem login. Vai direto pra api (o endpoint /sair é AllowAnonymous)."
    echo "    handle @optout {"
    echo "        reverse_proxy 127.0.0.1:${API_PORTS[$i]}"
    echo "    }"
    echo "    handle {"
    echo "        forward_auth ${GATE_UP} {"
    echo "            uri /authz"
    echo "        }"
    echo "        # A api já emite Access-Control-Allow-Origin (Web:Origins inclui o app.); aqui só"
    echo "        # complementamos o Allow-Credentials que falta nela, pra o fetch com cookie ser aceito."
    echo "        # (Se o Caddy também emitisse o ACAO, viria duplicado e o browser recusaria.)"
    echo "        header Access-Control-Allow-Credentials \"true\""
    echo "        reverse_proxy @api 127.0.0.1:${API_PORTS[$i]}"
    echo "        reverse_proxy 127.0.0.1:${WEB_PORTS[$i]}"
    echo "    }"
    echo "}"
    echo "# Tela do Android (noVNC) — atrás do portão."
    echo "phone-${L}.${MTRX_DOMAIN} {"
    fauth
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
# Botão "Sair" → logout do portão de autenticação.
sed -i "s#%%LOGOUT_URL%%#https://auth.${MTRX_DOMAIN}/logout#g" landing/index.html
# Credenciais pré-preenchidas por ambiente (o portão de autenticação já protege o acesso).
CREDS='<script>window.__MTRX_CREDS={'
for i in "${!LETTERS[@]}"; do
  L=${LETTERS[$i]}; n=$((i+1))
  if [ "$n" = 1 ]; then em=$(getenv SEED_ADMIN_EMAIL || true); pw=$(getenv SEED_ADMIN_PASS || true)
  else em=$(getenv "SEED${n}_ADMIN_EMAIL" || true); pw=$(getenv "SEED${n}_ADMIN_PASS" || true); fi
  em=${em:-admin-${L}@local}
  CREDS="${CREDS}${L}:{email:\"${em}\",pass:\"${pw}\"},"
done
CREDS="${CREDS}};</script>"
sed -i "s#<!--MTRX_CREDS-->#${CREDS}#" landing/index.html
echo "✓ deploy/landing/ gerado (URLs + credenciais pré-preenchidas + botão Sair)."
