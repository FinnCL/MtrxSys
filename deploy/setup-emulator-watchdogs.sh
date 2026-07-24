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
# Roda UMA vez, com sudo (instala unit no systemd). Idempotente — pode repetir. O ambiente A NÃO é tocado:
# ele segue no serviço legado mtrx-emulator-watchdog.service.
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
[ -f "$GUEST_GOST" ]   || echo "AVISO: $GUEST_GOST não existe — sem ele o proxy in-guest NÃO monta e o gate segura o disparo. Copie o binário do gost pra lá (o A já usa este mesmo)."

echo "== instalando o template mtrx-emulator-watchdog@.service =="
cp "$TEMPLATE_SRC" "$UNIT_DST"
systemctl daemon-reload

echo "== ligando os watchdogs dos stacks 2..10 (ociosos até você provisionar o emulador de cada um) =="
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
