#!/usr/bin/env bash
# Gera deploy/.env.prod a partir do .example, preenchendo cada __GEN__ com um
# segredo aleatório único (openssl) e a senha/hash do basic-auth do noVNC.
# Roda UMA vez. Não sobrescreve um .env.prod já existente.
set -euo pipefail
cd "$(dirname "$0")"

OUT=.env.prod
if [ -f "$OUT" ]; then
  echo "Já existe deploy/.env.prod — não vou sobrescrever."
  echo "Apague-o (rm deploy/.env.prod) se quiser regenerar do zero."
  exit 1
fi
command -v openssl >/dev/null || { echo "ERRO: openssl não encontrado."; exit 1; }
command -v docker  >/dev/null || { echo "ERRO: docker não encontrado.";  exit 1; }

# Cada __GEN__ vira um segredo distinto (24 bytes = 48 chars hex, > 32 do JWT).
awk '{
  while (match($0, /__GEN__/)) {
    cmd="openssl rand -hex 24"; cmd | getline s; close(cmd);
    $0 = substr($0,1,RSTART-1) s substr($0,RSTART+RLENGTH);
  }
  print
}' .env.prod.example > "$OUT"

# Basic-auth do noVNC (aba Celular / phone-*): senha aleatória + hash bcrypt.
PHONE_PASS=$(openssl rand -hex 12)
PHONE_HASH=$(docker run --rm caddy:2-alpine caddy hash-password --plaintext "$PHONE_PASS")
esc=$(printf '%s' "$PHONE_HASH" | sed -e 's/[&/\]/\\&/g')
sed -i "s#^MTRX_PHONE_BASIC_HASH=.*#MTRX_PHONE_BASIC_HASH=${esc}#" "$OUT"

chmod 600 "$OUT"
echo "──────────────────────────────────────────────────────────────"
echo "deploy/.env.prod gerado (chmod 600, fora do git)."
echo
echo "  noVNC (tela do Android)  usuário: operador   senha: ${PHONE_PASS}"
echo "  >> ANOTE essa senha — só o hash fica no .env.prod."
echo "──────────────────────────────────────────────────────────────"
echo "AGORA edite deploy/.env.prod e preencha:"
echo "  • MTRX_DOMAIN e MTRX_TLS_EMAIL"
echo "  • PHONE_WA_APK_URL_1..10  (URL do APK do WhatsApp)"
echo "  • WAHA_PROXY_1..10        (quando contratar os proxies; ver docs/proxy.md)"
