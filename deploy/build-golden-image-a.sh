#!/usr/bin/env bash
# Constrói a IMAGEM-OURO do emulador do stack A: um Android com Google e WhatsApp prontos que
# NUNCA registrou número nenhum. É o molde do qual todo emulador limpo nasce daqui pra frente.
#
# POR QUE ISTO EXISTE (2026-07-25): o A guarda o estado no PRÓPRIO container (commit-to-image, sem
# volume), e a tag `budtmo/docker-android:emulator_14.0` foi sendo sobrescrita com commits do device
# EM USO. Resultado: essa "imagem base" carrega a identidade de 05-06/07 — `android_id`, checkin do
# GSF, `phoneid` do WhatsApp e restos do número anterior. Todo `rebuild` restaurava essa bagagem, e o
# aparelho continuava sendo "o mesmo" pra quem olha de fora. Em 25/07 um chip SAUDÁVEL foi restringido
# 6h30 depois de registrar nesse device, PARADO, sem enviar nada — com a condição de correlação-por-
# device (escrita em 24/07, ANTES do fato) satisfeita. Este script quebra essa herança.
#
# O PASSO QUE DEFINE TUDO é o 6: commitar ANTES do primeiro registro. Um minuto de diferença na ordem
# separa um aparelho sem passado de um aparelho com ficha.
#
# ⚠️ Roda UM EMULADOR A MAIS em paralelo (o temporário) — pesado em CPU/RAM. O host do A aguenta
#    (125G RAM), mas evite rodar junto com disparo ativo.
# ⚠️ SÓ pro stack A (compose-managed). B-J são docker-run provisionados pela aba.
set -u

UPSTREAM=budtmo/docker-android:emulator_14.0
GOLDEN=mtrx-android:golden
LIVE=mtrx-android:live
TMP=mtrx-dandroid-golden
ATUAL=mtrx-dandroid
EMULATOR_PY=/home/ubuntu/emulator.py
STAMP=$(date -u +%Y%m%d-%H%M)

say() { echo "[ouro] $*"; }
die() { echo "[ouro] ERRO: $*" >&2; exit 1; }

command -v docker >/dev/null || die "docker não encontrado."
[ -f "$EMULATOR_PY" ] || die "$EMULATOR_PY não existe — é o mount que o compose do A usa (self-healing do AVD)."

if docker image inspect "$GOLDEN" >/dev/null 2>&1; then
  say "ATENÇÃO: $GOLDEN já existe (criada em $(docker image inspect "$GOLDEN" --format '{{.Created}}'))."
  printf "[ouro] Sobrescrever o molde? Isso descarta a referência limpa atual. (digite SIM): "
  read -r ok; [ "$ok" = "SIM" ] || { say "abortado."; exit 0; }
fi

# ── PASSO 0: backup da imagem suja ────────────────────────────────────────────────────────────────
# CRÍTICO: o `docker pull` do passo 1 SOBRESCREVE a tag local — e hoje essa tag É o estado do device
# (commit-to-image). Sem este backup, um pull destrói o aparelho atual sem aviso.
say "0/8 backup da imagem atual -> mtrx-android:backup-$STAMP"
docker tag "$UPSTREAM" "mtrx-android:backup-$STAMP" 2>/dev/null \
  || say "    (sem imagem local pra salvar — seguindo)"

# ── PASSO 1: imagem de fábrica ────────────────────────────────────────────────────────────────────
say "1/8 baixando a imagem ORIGINAL do docker-android (pode demorar)..."
docker pull "$UPSTREAM" >/dev/null || die "falha no pull de $UPSTREAM."

# ── PASSO 2: container temporário, mesma config do compose do A ───────────────────────────────────
# Sem publicar porta: só falamos com ele por `docker exec`. GPU swangle (o swiftshader crasha o qemu
# ao abrir o WhatsApp — provado 24/07). Entrypoint espelha o self-healing do X-lock do emulator-a.yml.
say "2/8 subindo o emulador temporário ($TMP)..."
docker rm -f "$TMP" >/dev/null 2>&1
docker run -d --name "$TMP" \
  --device /dev/kvm:/dev/kvm \
  -v "$EMULATOR_PY":/home/androidusr/docker-android/cli/src/device/emulator.py \
  -e EMULATOR_DEVICE="Samsung Galaxy S10" \
  -e WEB_VNC=true -e EMULATOR_NO_SKIN=true \
  -e SCREEN_WIDTH=404 -e SCREEN_HEIGHT=850 -e SCREEN_DEPTH=24 \
  -e EMULATOR_ADDITIONAL_ARGS="-gpu swangle_indirect" \
  --entrypoint sh "$UPSTREAM" \
  -c 'rm -f /tmp/.X*-lock 2>/dev/null; rm -rf /tmp/.X11-unix/* /home/androidusr/emulator/*.lock 2>/dev/null; exec "$APP_PATH"/mixins/scripts/run.sh' \
  >/dev/null || die "não subiu o container temporário."

say "3/8 aguardando o boot (cold boot ~2-3 min)..."
booted=0
for i in $(seq 1 30); do
  sleep 10
  b=$(docker exec "$TMP" adb -s emulator-5554 shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
  [ "$b" = "1" ] && { booted=1; say "    booted em ~$((i * 10))s."; break; }
done
[ "$booted" = 1 ] || { docker logs --tail 30 "$TMP"; die "não bootou em 300s (logs acima)."; }

# ── PASSO 4: deixar o GMS fazer checkin ───────────────────────────────────────────────────────────
# É aqui que nasce a identidade Google NOVA (GSF/android_id). Sem esperar, a imagem sai com um device
# meio-registrado e o WhatsApp pode reclamar depois. Não há sinal síncrono — damos tempo e checamos.
say "4/8 dando tempo pro checkin do Google (60s)..."
sleep 60
docker exec "$TMP" adb shell "su 0 ls /data/data/com.google.android.gsf/databases/ 2>/dev/null" 2>/dev/null \
  | grep -q gservices && say "    GSF presente." || say "    AVISO: GSF não confirmado — siga, mas anote."

# ── PASSO 5: instalar o WhatsApp ──────────────────────────────────────────────────────────────────
# APK extraído do container ATUAL pra a versão bater com a que já se sabe funcionar. WhatsApp é split
# APK (base + splits de idioma/densidade) -> pm path devolve várias linhas -> install-multiple.
# `-i com.android.vending` finge instalação pela Play Store (o sideload cru já foi suspeito de
# "Baixe o app oficial"; a hipótese NÃO está provada, mas o flag é grátis e não atrapalha).
say "5/8 instalando o WhatsApp (APK extraído de $ATUAL)..."
if ! docker exec "$ATUAL" adb shell "pm path com.whatsapp" >/dev/null 2>&1; then
  die "não achei o WhatsApp em $ATUAL pra copiar o APK. Suba o emulador atual ou instale à mão no $TMP."
fi
rm -rf /tmp/wa-apk && mkdir -p /tmp/wa-apk
paths=$(docker exec "$ATUAL" adb shell "pm path com.whatsapp" 2>/dev/null | tr -d '\r' | sed 's/^package://')
[ -n "$paths" ] || die "pm path voltou vazio."
n=0
for p in $paths; do
  n=$((n + 1))
  docker exec "$ATUAL" adb pull "$p" "/tmp/split-$n.apk" >/dev/null 2>&1 || die "falha no adb pull de $p"
  docker cp "$ATUAL:/tmp/split-$n.apk" "/tmp/wa-apk/split-$n.apk" >/dev/null 2>&1 || die "falha no docker cp de $p"
  docker cp "/tmp/wa-apk/split-$n.apk" "$TMP:/tmp/split-$n.apk" >/dev/null 2>&1 || die "falha ao copiar pro $TMP"
done
say "    $n APK(s) copiados; instalando..."
lst=""; for i in $(seq 1 $n); do lst="$lst /tmp/split-$i.apk"; done
docker exec "$TMP" adb install-multiple -i com.android.vending $lst >/dev/null 2>&1 \
  || docker exec "$TMP" adb install -i com.android.vending /tmp/split-1.apk >/dev/null 2>&1 \
  || die "instalação do WhatsApp falhou."
docker exec "$TMP" adb shell "pm list packages | grep -q com.whatsapp" 2>/dev/null \
  || die "WhatsApp não aparece instalado após o install."

# Pré-concede as permissões que o fluxo de chip novo precisa. Registrar RESETA isso (por isso o
# passo também está no checklist pós-registro), mas já deixar no molde evita esquecer no caso comum.
say "    pré-concedendo câmera/contatos..."
for perm in CAMERA READ_CONTACTS WRITE_CONTACTS; do
  docker exec "$TMP" adb shell "pm grant com.whatsapp android.permission.$perm" >/dev/null 2>&1
done

# ── PASSO 6: commitar ANTES de qualquer registro ──────────────────────────────────────────────────
say "6/8 ⛔ NÃO abrimos o WhatsApp. Commitando o molde -> $GOLDEN"
docker commit "$TMP" "$GOLDEN" >/dev/null || die "docker commit falhou."

say "7/8 removendo o temporário..."
docker rm -f "$TMP" >/dev/null 2>&1

# `:live` é a tag de TRABALHO (o compose aponta pra ela). Se ainda não existe, nasce do molde.
if ! docker image inspect "$LIVE" >/dev/null 2>&1; then
  say "8/8 criando a tag de trabalho $LIVE a partir do molde"
  docker tag "$GOLDEN" "$LIVE"
else
  say "8/8 $LIVE já existe (estado de trabalho atual) — preservada."
fi

cat <<EOF

[ouro] ✅ MOLDE PRONTO: $GOLDEN (Android + Google + WhatsApp, ZERO registros)
[ouro]    backup da imagem antiga: mtrx-android:backup-$STAMP

   Daqui pra frente:
     • chip morreu  ->  bash deploy/limpar-emulador-a.sh      (aparelho novo, do molde)
     • chip registrado e funcionando  ->  bash deploy/salvar-estado-emulador-a.sh   (salva em $LIVE)

   O molde NUNCA é usado direto e NUNCA recebe commit. Trabalha-se sempre em cópias.
EOF
