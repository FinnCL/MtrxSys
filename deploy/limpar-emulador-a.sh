#!/usr/bin/env bash
# APARELHO NOVO no stack A: recria o emulador a partir da IMAGEM-OURO (molde que nunca registrou
# número), em vez de restaurar o estado acumulado. É o comando pra rodar QUANDO UM CHIP MORRE.
#
# Diferença pro `rebuild-emulator-a.sh`: aquele recria do estado de TRABALHO (`mtrx-android:live`) e
# serve pra consertar emulador quebrado PRESERVANDO o chip. Este aqui joga o estado fora de propósito
# e devolve um device SEM HISTÓRICO — `android_id` novo, checkin do Google novo, sem `phoneid`, sem
# rastro do número anterior. É a única forma de o próximo chip não herdar a ficha do chip queimado.
#
# Pré-requisito: `bash deploy/build-golden-image-a.sh` (uma vez).
set -u

GOLDEN=mtrx-android:golden
LIVE=mtrx-android:live
CONTAINER=mtrx-dandroid
PROJECT=mtrxsys
COMPOSE=deploy/docker-compose.emulator-a.yml

say() { echo "[limpar-A] $*"; }
die() { echo "[limpar-A] ERRO: $*" >&2; exit 1; }

cd "$(dirname "$0")/.." || die "não achei a raiz do repo"
[ -f "$COMPOSE" ] || die "$COMPOSE não encontrado — rode da raiz do MtrxSys"

docker image inspect "$GOLDEN" >/dev/null 2>&1 \
  || die "não existe $GOLDEN. Construa o molde primeiro: bash deploy/build-golden-image-a.sh"

# ── GUARDA: não jogar fora um chip que está VIVO ───────────────────────────────────────────────────
# `registration_jid` vazio = WhatsApp deslogado (é o mesmo sinal que o GetWhatsAppNumberAsync do app
# lê). Se vier PREENCHIDO, existe chip logado aqui e limpar custaria esse número — pede confirmação
# explícita. adb mudo/container fora = "não sei": segue sem barrar (fail-open só na dúvida).
num=$(docker exec "$CONTAINER" adb shell \
        "grep -ah registration_jid /data/data/com.whatsapp/shared_prefs/*.xml 2>/dev/null" 2>/dev/null \
      | grep -oE '[0-9]{12,13}' | head -1)
if [ -n "${num:-}" ]; then
  say "⚠️  TEM CHIP LOGADO neste emulador: +$num"
  say "    Limpar vai PERDER esse número (ele teria que ser registrado de novo, e cada registro é"
  say "    um novo julgamento do WhatsApp). Só siga se ele já estiver restringido/inutilizável."
  printf "[limpar-A] Confirma apagar? (digite SIM): "
  read -r ok; [ "$ok" = "SIM" ] || { say "abortado — nada foi tocado."; exit 0; }
fi

say "1/4 apontando o estado de trabalho pro molde ($GOLDEN -> $LIVE)"
docker tag "$GOLDEN" "$LIVE" || die "falha ao retagar."

say "2/4 apagando o container atual..."
docker rm -f "$CONTAINER" >/dev/null 2>&1

say "3/4 recriando pelo compose (projeto $PROJECT — rede/porta/self-healing certos)..."
docker compose -p "$PROJECT" -f "$COMPOSE" up -d dandroid \
  || die "falha ao subir pelo compose — cheque o docker no host."

say "4/4 aguardando o boot (cold boot ~2-3 min)..."
booted=0
for i in $(seq 1 20); do
  sleep 10
  b=$(docker exec "$CONTAINER" adb -s emulator-5554 shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
  [ "$b" = "1" ] && { booted=1; say "    BOOTED em ~$((i * 10))s."; break; }
done
[ "$booted" = 1 ] || { say "não bootou em 200s. Diagnostique: docker logs --tail 30 $CONTAINER"; exit 1; }

# O watchdog monta o gost+iptables DENTRO do Android ~20s após o boot. Registrar antes disso manda o
# número pelo IP do datacenter canadense (ban). Esperamos e CONFIRMAMOS em vez de mandar "aguarde".
say "    esperando o watchdog montar o proxy in-guest..."
proxy=0
for i in $(seq 1 12); do
  sleep 10
  if docker exec "$CONTAINER" adb shell \
       "su 0 iptables -t nat -C OUTPUT -p tcp -j REDIRECT --to-ports 12345 && su 0 ss -ltn 2>/dev/null | grep -q :12345" \
       >/dev/null 2>&1; then proxy=1; break; fi
done

cat <<EOF

[limpar-A] ✅ APARELHO NOVO DE PÉ — sem histórico, pronto pra receber um chip.
EOF
if [ "$proxy" = 1 ]; then
  echo "[limpar-A] ✅ Proxy in-guest ATIVO — seguro registrar (egresso sai pelo residencial BR)."
else
  echo "[limpar-A] ⛔ Proxy in-guest NÃO confirmado. NÃO REGISTRE AINDA — o número sairia pelo IP do"
  echo "           datacenter. Espere o badge 'Proxy do emulador: OK' na aba Celular ou cheque:"
  echo "           journalctl -u mtrx-emulator-watchdog -n 30"
fi
cat <<EOF

   AGORA, nesta ordem:
     1. Registre o chip novo (UMA vez — cada registro é um novo julgamento).
     2. Reconceda as permissões (o registro RESETA):
          docker exec $CONTAINER adb shell pm grant com.whatsapp android.permission.READ_CONTACTS
          docker exec $CONTAINER adb shell pm grant com.whatsapp android.permission.WRITE_CONTACTS
        Sem isso o espelho fica vazio, todo número vira "não tem WhatsApp" e o disparo dá 0 envios.
     3. RE-IMPORTE os grupos com o chip novo (o gate por chip pula os contatos do chip antigo).
     4. bash deploy/salvar-estado-emulador-a.sh    <- salva o estado bom (sobrevive a crash)
     5. Não mexa mais: nada de reset/recreate/adb pesado. Cada mexida é uma chance de perder.
EOF
