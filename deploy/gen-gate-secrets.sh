#!/usr/bin/env bash
# Gera/rotaciona a credencial do PORTÃO (MtrxSys.Gate) em deploy/.env.prod:
#   • senha aleatória (mostrada UMA vez)
#   • hash PBKDF2 no formato iters.b64salt.b64hash — o MESMO que o MtrxSys.Gate verifica
#     (VerifyPassword: PBKDF2-SHA256, ver src/MtrxSys.Gate/Program.cs)
#   • segredo TOTP (base32) pro 2FA
# Atualiza as linhas GATE_* se já existirem, senão anexa. Rode quando quiser (re)definir
# a senha do portão — depois configure o 2FA em  https://auth.<domínio>/enroll .
#   Uso:  bash deploy/gen-gate-secrets.sh [caminho-do-.env]   (default: deploy/.env.prod)
set -euo pipefail
cd "$(dirname "$0")"
OUT=${1:-.env.prod}
[ -f "$OUT" ] || { echo "ERRO: $OUT não existe (rode deploy/gen-secrets.sh primeiro)."; exit 1; }
command -v openssl >/dev/null || { echo "ERRO: openssl não encontrado."; exit 1; }
PY_BIN=$(command -v python3 || command -v python || true)
[ -n "$PY_BIN" ] || { echo "ERRO: python3/python não encontrado (necessário p/ o hash PBKDF2 do portão)."; exit 1; }

GATE_PASS=$(openssl rand -hex 12)
# Deriva o hash + TOTP em python (hashlib): sai "HASH TOTP" numa linha. A senha entra por
# env (GP=) pra não aparecer na linha de comando/ps.
GATE_GEN=$(GP="$GATE_PASS" "$PY_BIN" - <<'PY'
import os, hashlib, base64
pw    = os.environ["GP"].encode()
salt  = os.urandom(16)
iters = 210000
h     = hashlib.pbkdf2_hmac("sha256", pw, salt, iters)
totp  = base64.b32encode(os.urandom(20)).decode().rstrip("=")
print(f"{iters}.{base64.b64encode(salt).decode()}.{base64.b64encode(h).decode()} {totp}")
PY
)
GATE_HASH=${GATE_GEN% *}      # tudo antes do espaço
GATE_TOTP=${GATE_GEN##* }     # tudo depois do espaço

# Atualiza-ou-anexa cada chave. Delimitador '#' no sed (base64/base32 nunca contêm '#');
# escapamos & \ # no lado direito (especiais na substituição).
set_kv() {
  local key=$1 val=$2 esc
  esc=$(printf '%s' "$val" | sed -e 's/[&\\#]/\\&/g')
  if grep -Eq "^${key}=" "$OUT"; then
    sed -i "s#^${key}=.*#${key}=${esc}#" "$OUT"
  else
    printf '%s=%s\n' "$key" "$val" >> "$OUT"
  fi
}
set_kv GATE_USER admin
set_kv GATE_PASSWORD_HASH "$GATE_HASH"
set_kv GATE_TOTP_SECRET "$GATE_TOTP"

echo "──────────────────────────────────────────────────────────────"
echo "Portão de autenticação gravado em $OUT:"
echo "  usuário: admin"
echo "  senha:   ${GATE_PASS}"
echo "  >> ANOTE a senha — só o hash fica no arquivo (não dá pra recuperar)."
echo "  2FA: depois de subir, abra  https://auth.<seu-domínio>/enroll  (pede a senha, mostra o QR)."
echo "──────────────────────────────────────────────────────────────"
