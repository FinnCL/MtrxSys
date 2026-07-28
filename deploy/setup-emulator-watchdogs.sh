#!/usr/bin/env bash
# Instala e LIGA os watchdogs de emulador por-stack (2..10) — a peça que torna o emulador de cada
# ambiente AUTO-PROTEGIDO e AUTO-RECUPERADO como o ambiente A, de modo dinâmico. Cada watchdog@N:
#   • monta o proxy in-guest (gost + iptables nat OUTPUT) DENTRO do Android → egresso sai pelo residencial
#     (WAHA_PROXY_N), nunca pelo IP do datacenter — e escreve o flag de saúde que o gate do disparo lê;
#   • recupera o emulador de crash e mantém a tela limpa.
# Fica OCIOSO enquanto o stack não tem emulador (o loop vê "container inexistente" e não faz nada), então
# ligá-lo ANTES de provisionar é seguro — e é o certo: quando você provisiona (aba Celular → Provisionar),
# o proxy já sobe sozinho ANTES de você registrar o chip (senão o número sairia pelo datacenter = ban).
#
# AUTO-RODADO pelo up-all-prod.sh (passo [3.5/5], sudo sem senha) em TODO deploy — idempotente, então um
# servidor NOVO fica protegido só com o deploy, sem passo manual. Também baixa o gost-guest se faltar e
# cobre o A (@1) num servidor novo. Dá pra rodar à mão também:
#   sudo bash deploy/setup-emulator-watchdogs.sh
set -euo pipefail
cd "$(dirname "$0")/.."   # raiz do repo (no servidor: /home/ubuntu/MtrxSys)

TEMPLATE_SRC=deploy/mtrx-emulator-watchdog@.service
UNIT_DST=/etc/systemd/system/mtrx-emulator-watchdog@.service
SCRIPT="$(pwd)/deploy/emulator-watchdog.sh"    # o que o template chama (com $1=N)
GUEST_GOST=/home/ubuntu/gost-guest             # binário do gost empurrado PRA DENTRO do Android

# Pré-condições (fail-closed): sem elas o watchdog subiria mas não protegeria — melhor abortar loud.
[ -f "$TEMPLATE_SRC" ] || { echo "ERRO: $TEMPLATE_SRC não existe (deploye o código antes)."; exit 1; }
[ -f "$SCRIPT" ]       || { echo "ERRO: $SCRIPT não existe (deploye o código antes)."; exit 1; }

# gost-guest (o gost que o watchdog empurra PRA DENTRO do Android): baixa se faltar, pra um servidor NOVO
# não precisar do binário à mão. Estático linux/amd64 (o emulador é x86_64), versão PINADA. Se falhar,
# avisa loud — sem ele o proxy in-guest não monta (o gate fail-closed segura o disparo, então não vaza).
GOST_VER=3.0.0
if [ ! -f "$GUEST_GOST" ]; then
  echo "gost-guest ausente — baixando gost ${GOST_VER} (estático linux/amd64) das releases do go-gost…"
  if curl -fsSL "https://github.com/go-gost/gost/releases/download/v${GOST_VER}/gost_${GOST_VER}_linux_amd64.tar.gz" \
       | tar -xz -C /tmp gost 2>/dev/null && mv /tmp/gost "$GUEST_GOST"; then
    echo "  ✓ gost-guest obtido em $GUEST_GOST."
  else
    echo "  ⚠ FALHA ao baixar o gost-guest. Copie de um servidor que funcione (/home/ubuntu/gost-guest) ou"
    echo "    baixe à mão das releases do go-gost/gost (asset linux_amd64). Sem ele o proxy in-guest NÃO sobe."
  fi
fi

echo "== instalando o template mtrx-emulator-watchdog@.service =="
cp "$TEMPLATE_SRC" "$UNIT_DST"
systemctl daemon-reload

echo "== ligando os watchdogs de emulador (ociosos até você provisionar o emulador de cada stack) =="
# Ambiente A: MIGRAR do serviço legado (unit sem @, script CONGELADO em /home/ubuntu/) pro @1
# (script do repo, que o deploy atualiza).
#
# 🔴 POR QUE ISTO MUDOU (2026-07-28). A versão anterior fazia "se o legado já roda, não mexe —
# intocado". A intenção era não derrubar o servidor atual no meio da migração. O efeito foi o oposto:
# o legado NUNCA saía, então o A rodou por semanas uma cópia de 25/jul, SEM ensure_wa_permissions —
# e todo chip novo travava na tela de backup do Google Drive (falta GET_ACCOUNTS). "Não mexer" virou
# "congelar pra sempre". Preservar só faz sentido enquanto o legado está ATUALIZADO; desatualizado,
# preservar é o bug.
if systemctl is-active --quiet mtrx-emulator-watchdog.service 2>/dev/null; then
  echo "   ambiente A: serviço legado ativo — migrando pro @1 (script do repo)…"
  # Desliga o legado ANTES de subir o @1: os dois mexem no MESMO container (mtrx-dandroid), e dois
  # watchdogs disputando o aparelho é pior que um. Ordem importa.
  systemctl disable --now mtrx-emulator-watchdog.service >/dev/null 2>&1 || true
fi
systemctl enable --now mtrx-emulator-watchdog@1 >/dev/null 2>&1 || true
echo "   mtrx-emulator-watchdog@1  (ambiente A) -> $(systemctl is-active mtrx-emulator-watchdog@1 2>/dev/null || echo '?')"
for n in 2 3 4 5 6 7 8 9 10; do
  systemctl enable --now "mtrx-emulator-watchdog@${n}" >/dev/null 2>&1 || true
  printf "   mtrx-emulator-watchdog@%-2s -> %s\n" "$n" "$(systemctl is-active "mtrx-emulator-watchdog@${n}" 2>/dev/null || echo '?')"
  # Aviso por-stack: sem o proxy residencial no .env, o watchdog roda mas NÃO protege o egresso.
  grep -qE "^WAHA_PROXY_${n}=" deploy/.env.prod 2>/dev/null || echo "      ⚠ WAHA_PROXY_${n} não está no .env.prod — configure o proxy ANTES de provisionar/registrar o chip do stack ${n}."
done

echo
echo "✓ Watchdogs 2..10 no ar. Fluxo pra cada stack ficar completo como o A:"
echo "   1) preencher WAHA_PROXY_<n>/_USER/_PASS no deploy/.env.prod (proxy residencial único);"
echo "   2) aba Celular do stack → 'Provisionar emulador' (cria o Android; o watchdog já monta o proxy);"
echo "   3) registrar o chip por SMS na tela; 4) aquecer ~10 dias antes de automatizar."
echo "   O A segue no serviço legado mtrx-emulator-watchdog.service (intocado)."
