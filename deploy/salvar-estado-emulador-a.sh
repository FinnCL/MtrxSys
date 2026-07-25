#!/usr/bin/env bash
# Salva o estado ATUAL do emulador do A na tag de trabalho (`mtrx-android:live`) — a rede de segurança
# do chip registrado. RODE DEPOIS DE REGISTRAR UM CHIP E DEIXAR TUDO FUNCIONANDO.
#
# POR QUE: o A não tem volume (volume no AVD trava o boot) — o estado vive no container. Se o
# container morrer sem um commit recente, o chip logado vai junto e você precisa REGISTRAR DE NOVO,
# que é justamente a operação cara (cada registro é um novo julgamento do WhatsApp, e foi num registro
# que o chip de 25/07 morreu). Um commit depois de registrar transforma "perdi o número" em "recrio e
# continuo".
#
# ⚠️ NUNCA commita por cima de `mtrx-android:golden`. O molde vale por nunca ser tocado — no instante
#    em que ele recebe o estado de um device em uso, deixa de ser molde e vira histórico, que é
#    exatamente o problema que a imagem-ouro existe pra resolver.
set -u

LIVE=mtrx-android:live
GOLDEN=mtrx-android:golden
CONTAINER=mtrx-dandroid
STAMP=$(date -u +%Y%m%d-%H%M)

say() { echo "[salvar-A] $*"; }
die() { echo "[salvar-A] ERRO: $*" >&2; exit 1; }

docker inspect "$CONTAINER" >/dev/null 2>&1 || die "container $CONTAINER não existe."
[ "$(docker inspect "$CONTAINER" --format '{{.State.Running}}' 2>/dev/null)" = "true" ] \
  || die "$CONTAINER não está rodando — suba antes de salvar."

# Só faz sentido salvar um estado COM chip. Sem `registration_jid` o que seria congelado é um
# aparelho deslogado — e aí o commit não te protege de nada, só sobrescreve um estado melhor.
num=$(docker exec "$CONTAINER" adb shell \
        "grep -ah registration_jid /data/data/com.whatsapp/shared_prefs/*.xml 2>/dev/null" 2>/dev/null \
      | grep -oE '[0-9]{12,13}' | head -1)
if [ -z "${num:-}" ]; then
  say "⚠️  Nenhum chip registrado aqui (registration_jid vazio) — não há estado útil a salvar."
  printf "[salvar-A] Salvar assim mesmo? (digite SIM): "
  read -r ok; [ "$ok" = "SIM" ] || { say "abortado."; exit 0; }
else
  say "chip logado: +$num"
fi

# Versão anterior fica guardada: se o commit congelar um estado ruim, dá pra voltar.
if docker image inspect "$LIVE" >/dev/null 2>&1; then
  say "guardando o $LIVE anterior como mtrx-android:live-$STAMP"
  docker tag "$LIVE" "mtrx-android:live-$STAMP"
fi

say "commitando $CONTAINER -> $LIVE ..."
docker commit "$CONTAINER" "$LIVE" >/dev/null || die "docker commit falhou."

docker image inspect "$GOLDEN" >/dev/null 2>&1 \
  && say "molde $GOLDEN intacto (como deve ser)." \
  || say "⚠️  $GOLDEN não existe — construa com deploy/build-golden-image-a.sh."

# PODA: a imagem do emulador tem ~5,7 GB e este script roda a cada chip registrado — sem limite, as
# versões `live-*` encheriam o disco em silêncio (o pior modo de falha num servidor que hospeda os 10
# stacks). Guarda as 2 mais recentes: uma pra voltar de um commit ruim, outra de folga. As tags têm
# formato `live-YYYYmmdd-HHMM`, então ordem lexicográfica reversa == mais novas primeiro.
# `docker rmi` falha silenciosamente se a imagem estiver em uso — o que é o comportamento desejado.
docker images --format '{{.Tag}}' --filter "reference=mtrx-android" 2>/dev/null \
  | grep '^live-[0-9]' | sort -r | tail -n +3 \
  | while read -r t; do
      docker rmi "mtrx-android:$t" >/dev/null 2>&1 && say "poda: removida mtrx-android:$t"
    done

cat <<EOF

[salvar-A] ✅ Estado salvo em $LIVE.
           A partir de agora, um crash não custa o chip: deploy/rebuild-emulator-a.sh
           recria deste ponto. Pra APARELHO NOVO (chip morto), use deploy/limpar-emulador-a.sh.
EOF
