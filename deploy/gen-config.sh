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
# Domínio (da marca) usado SÓ no link de opt-out das mensagens, pra mascarar o nome do sistema pro
# destinatário. Opcional: se vazio, o link continua saindo no domínio do próprio stack (<letra>.<MTRX_DOMAIN>).
MTRX_OPTOUT_DOMAIN=$(getenv MTRX_OPTOUT_DOMAIN)
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
  # no-store: a landing NÃO pode ser cacheada. Se o navegador a servir do cache (favorito), a
  # requisição não chega no Caddy e o forward_auth é PULADO → o portão vira decorativo e um "Sair"
  # não re-desafia. Forçando revalidação, toda abertura passa pelo /authz (deslogado → login).
  echo "    header Cache-Control \"no-store\""
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
    echo "    @optout path /sair* /s/*"
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
    echo "        # A api já emite Access-Control-Allow-Origin (Web:Origins inclui o app.) E"
    echo "        # Access-Control-Allow-Credentials:true (CORS .AllowCredentials no Program.cs)."
    echo "        # Por isso o Caddy NÃO deve reemitir nenhum dos dois: viria duplicado e o browser recusaria."
    echo "        reverse_proxy @api 127.0.0.1:${API_PORTS[$i]}"
    echo "        reverse_proxy 127.0.0.1:${WEB_PORTS[$i]}"
    echo "    }"
    echo "}"
    # Porta da tela do Android (noVNC do docker-android). O stack A usa o docker-android como PRIMÁRIO
    # (docker-compose.emulator-a.yml), cujo noVNC publica no host 6090 (não no 6080 do array, pra não
    # colidir com o base). Os demais stacks usam NOVNC_PORTS[i] (6080-6089). O 8000 do ws-scrcpy era do
    # experimento redroid (abandonado: bug de foco de input) — não usar.
    PHONE_PORT=${NOVNC_PORTS[$i]}
    [ "$i" = "0" ] && PHONE_PORT=6090
    echo "# Tela do Android (noVNC/ws-scrcpy) — atrás do portão."
    echo "phone-${L}.${MTRX_DOMAIN} {"
    fauth
    echo "    reverse_proxy 127.0.0.1:${PHONE_PORT}"
    echo "}"
    # Subdomínio da MARCA pro link de opt-out (mascara o nome do sistema pro destinatário). Serve SÓ o
    # descadastro (/sair e /s) deste stack, roteado pra api dele; qualquer outro caminho → 404 (nada de
    # app/login exposto aqui). Guardado por MTRX_OPTOUT_DOMAIN: sem ele, nada é gerado e o link continua
    # saindo em <letra>.<MTRX_DOMAIN>. O token é por-stack, então cada letra bate na api do seu stack.
    if [ -n "$MTRX_OPTOUT_DOMAIN" ]; then
      echo "# Opt-out da marca — SÓ o link de descadastro deste stack (sem app, sem login)."
      echo "${L}.${MTRX_OPTOUT_DOMAIN} {"
      echo "    @optout path /sair* /s/*"
      echo "    handle @optout {"
      echo "        reverse_proxy 127.0.0.1:${API_PORTS[$i]}"
      echo "    }"
      echo "    handle {"
      echo "        respond 404"
      echo "    }"
      echo "}"
    fi
  done

  # ── HOMOLOGAÇÃO (staging) — mesmo padrão dos ambientes, apontando pro stack ISOLADO mtrxhml
  #    (api 127.0.0.1:5190 / web 127.0.0.1:5191). Sem tela de Android (homolog é WahaOnly). Se o
  #    stack de homolog não estiver no ar, só o hml.<domínio> dá 502 — os 10 não são afetados. ──
  echo
  echo "# Homologação (dashboard + api mesma-origem) — atrás do portão + CORS p/ a landing."
  echo "hml.${MTRX_DOMAIN} {"
  echo "    @preflight method OPTIONS"
  echo "    @api path /api/* /webhooks/* /health*"
  echo "    @optout path /sair* /s/*"
  echo "    handle @preflight {"
  echo "        header Access-Control-Allow-Origin \"https://app.${MTRX_DOMAIN}\""
  echo "        header Access-Control-Allow-Credentials \"true\""
  echo "        header Access-Control-Allow-Methods \"GET, POST, OPTIONS\""
  echo "        header Access-Control-Allow-Headers \"Content-Type\""
  echo "        header Access-Control-Max-Age \"600\""
  echo "        respond 204"
  echo "    }"
  echo "    # Opt-out PÚBLICO (LGPD): o /sair não passa pelo portão (o destinatário não tem login)."
  echo "    handle @optout {"
  echo "        reverse_proxy 127.0.0.1:5190"
  echo "    }"
  echo "    handle {"
  echo "        forward_auth ${GATE_UP} {"
  echo "            uri /authz"
  echo "        }"
  echo "        reverse_proxy @api 127.0.0.1:5190"
  echo "        reverse_proxy 127.0.0.1:5191"
  echo "    }"
  echo "}"
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

# Fail-closed: nenhum placeholder %%...%% pode sobrar na landing gerada. Se o %%LOGOUT_URL%%
# escapasse cru, o botão "Sair" faria POST pra .../%%LOGOUT_URL%% → 404 silencioso, a sessão do
# portão NUNCA seria invalidada e o favorito passaria direto mesmo "deslogado". Aborta o deploy.
if grep -qE '%%[A-Z_]+%%' landing/index.html; then
  echo "ERRO: sobrou placeholder não substituído na landing gerada — o 'Sair' (logout) quebraria:"
  grep -oE '%%[A-Z_]+%%' landing/index.html | sort -u | sed 's/^/  /'
  exit 1
fi
echo "✓ deploy/landing/ gerado (URLs + credenciais pré-preenchidas + botão Sair)."
