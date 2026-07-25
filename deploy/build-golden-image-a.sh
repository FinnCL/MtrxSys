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

# Sobe um emulador com a MESMA config do compose do A. Existia duplicado (temporário e verificação) —
# e config duplicada é config que diverge: bastaria alguém ajustar a GPU num lugar só pra o molde ser
# construído com um renderizador e verificado com outro, escondendo justamente o crash que o swangle
# conserta. Sem publicar porta: só falamos por `docker exec`.
run_emulator() {
  local name=$1 image=$2
  docker rm -f "$name" >/dev/null 2>&1
  docker run -d --name "$name" \
    --device /dev/kvm:/dev/kvm \
    -v "$EMULATOR_PY":/home/androidusr/docker-android/cli/src/device/emulator.py \
    -e EMULATOR_DEVICE="Samsung Galaxy S10" \
    -e WEB_VNC=true -e EMULATOR_NO_SKIN=true \
    -e SCREEN_WIDTH=404 -e SCREEN_HEIGHT=850 -e SCREEN_DEPTH=24 \
    -e EMULATOR_ADDITIONAL_ARGS="-gpu swangle_indirect" \
    --entrypoint sh "$image" \
    -c 'rm -f /tmp/.X*-lock 2>/dev/null; rm -rf /tmp/.X11-unix/* /home/androidusr/emulator/*.lock 2>/dev/null; exec "$APP_PATH"/mixins/scripts/run.sh' \
    >/dev/null
}

# Espera `sys.boot_completed=1`. Devolve 0/1 em vez de matar o script: cada chamador tem um cleanup
# diferente a fazer no fracasso (derrubar o temporário, ou DESTAGAR um molde recém-commitado que não
# presta). Ecoa o tempo pra o log dizer se o boot está degradando entre execuções.
wait_boot() {
  local name=$1 tries=${2:-30} i b
  for i in $(seq 1 "$tries"); do
    sleep 10
    b=$(docker exec "$name" adb -s emulator-5554 shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
    [ "$b" = "1" ] && { say "    booted em ~$((i * 10))s."; return 0; }
  done
  return 1
}

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
say "0/9 backup da imagem atual -> mtrx-android:backup-$STAMP"
docker tag "$UPSTREAM" "mtrx-android:backup-$STAMP" 2>/dev/null \
  || say "    (sem imagem local pra salvar — seguindo)"

# ── PASSO 1: imagem de fábrica ────────────────────────────────────────────────────────────────────
say "1/9 baixando a imagem ORIGINAL do docker-android (pode demorar)..."
docker pull "$UPSTREAM" >/dev/null || die "falha no pull de $UPSTREAM."

# ── PASSO 2: container temporário, mesma config do compose do A ───────────────────────────────────
# Sem publicar porta: só falamos com ele por `docker exec`. GPU swangle (o swiftshader crasha o qemu
# ao abrir o WhatsApp — provado 24/07). Entrypoint espelha o self-healing do X-lock do emulator-a.yml.
say "2/9 subindo o emulador temporário ($TMP)..."
run_emulator "$TMP" "$UPSTREAM" || die "não subiu o container temporário."

say "3/9 aguardando o boot (cold boot ~2-3 min)..."
wait_boot "$TMP" 30 || { docker logs --tail 30 "$TMP"; docker rm -f "$TMP" >/dev/null 2>&1; die "não bootou em 300s (logs acima)."; }

# ── PASSO 4: deixar o GMS fazer checkin ───────────────────────────────────────────────────────────
# É aqui que nasce a identidade Google NOVA (GSF/android_id). Sem esperar, a imagem sai com um device
# meio-registrado e o WhatsApp pode reclamar depois. Não há sinal síncrono — damos tempo e checamos.
say "4/9 dando tempo pro checkin do Google (60s)..."
sleep 60
docker exec "$TMP" adb shell "su 0 ls /data/data/com.google.android.gsf/databases/ 2>/dev/null" 2>/dev/null \
  | grep -q gservices && say "    GSF presente." || say "    AVISO: GSF não confirmado — siga, mas anote."

# LOCALE pt-BR ASSADO NO MOLDE. `persist.sys.locale` mora em /data/property, logo entra no commit e vale
# já no PRIMEIRO boot de todo aparelho novo: a tela de boas-vindas do WhatsApp nasce em português, sem
# precisar trocar o idioma na mão a cada limpeza. O watchdog já setava esse prop, mas só "aplica no
# próximo boot" (ele evita forçar framework restart de propósito — isso causou loop em 24/07), então o
# aparelho recém-limpo aparecia em inglês até alguém reiniciar. Com o prop no molde, o `ensure_locale`
# do watchdog encontra pt-BR e vira no-op.
# Sem conflito com o disparo: o envio pela UI acha o botão por RESOURCE-ID e normaliza o status de
# entrega de forma independente de idioma (ver WhatsAppSendResult) — nada depende do texto na tela.
#
# ⚠️ NÃO BASTA SETAR O PROP. O framework lê `persist.sys.locale` no INIT; setá-lo com o sistema já de pé
# não muda nada, e o commit congela um Android rodando em inglês (o emulador restaura por quick-boot, e
# ninguém relê o prop no boot seguinte). Sintoma exato medido em 25/07: `getprop persist.sys.locale` =
# pt-BR e `am get-config` = en-rUS, com a tela do WhatsApp em inglês. Por isso: setar o prop e REINICIAR
# o Android aqui, uma vez, pra o estado commitado já estar em português.
say "    setando locale pt-BR e reiniciando o Android pra ele valer no molde..."
docker exec "$TMP" adb shell "su 0 setprop persist.sys.locale pt-BR" >/dev/null 2>&1
docker exec "$TMP" adb reboot >/dev/null 2>&1
sleep 20
wait_boot "$TMP" 30 || { docker rm -f "$TMP" >/dev/null 2>&1; die "o emulador não voltou do reboot do locale."; }
cfg=$(docker exec "$TMP" adb shell "am get-config" 2>/dev/null | head -1)
case "$cfg" in
  *pt-rBR*) say "    locale EFETIVO pt-BR confirmado." ;;
  *)        say "    ⚠️ locale efetivo não é pt-BR ($cfg) — a tela de boas-vindas sairá em inglês." ;;
esac

# ── PASSO 5: instalar o WhatsApp ──────────────────────────────────────────────────────────────────
# APK extraído do container ATUAL pra a versão bater com a que já se sabe funcionar. WhatsApp é split
# APK (base + splits de idioma/densidade) -> pm path devolve várias linhas -> install-multiple.
# `-i com.android.vending` finge instalação pela Play Store (o sideload cru já foi suspeito de
# "Baixe o app oficial"; a hipótese NÃO está provada, mas o flag é grátis e não atrapalha).
say "5/9 instalando o WhatsApp (APK extraído de $ATUAL)..."
if ! docker exec "$ATUAL" adb shell "pm path com.whatsapp" >/dev/null 2>&1; then
  die "não achei o WhatsApp em $ATUAL pra copiar o APK. Suba o emulador atual ou instale à mão no $TMP."
fi
rm -rf /tmp/wa-apk && mkdir -p /tmp/wa-apk
# Limpa sobras de execuções anteriores DENTRO do container fonte. Sem isto o `adb pull` falha com rc=1:
# o arquivo antigo pode ter dono `ubuntu` (veio de um `docker cp`, que grava como root/ubuntu) enquanto o
# adb roda como `androidusr` — e ele não consegue sobrescrever. Custou uma execução do build em 25/07,
# com erro mudo ("falha no adb pull") que parecia problema de adb. `-u root` porque só root remove.
docker exec -u root "$ATUAL" sh -lc 'rm -f /tmp/split-*.apk' >/dev/null 2>&1
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

# ── PASSO 6: DESLIGAR o Android antes de commitar ─────────────────────────────────────────────────
# ⚠️ NÃO COMMITAR COM O EMULADOR RODANDO. O qemu mantém a escrita do `userdata-qemu.img` em buffer: um
# commit com ele vivo captura o filesystem ANTES de o app ter sido persistido. Sintoma exato (medido na
# 1ª execução deste script, 2026-07-25): o `pm list packages` confirmava o WhatsApp instalado, o commit
# rodava, e o container criado a partir do molde nascia SEM o app. `adb emu kill` desliga o emulador, e
# só então o qemu grava o userdata (e o snapshot de quick-boot).
# Tira o APK de dentro do molde ANTES de commitar. Não é só desperdício (138 MB por split): o arquivo
# nasce com dono `ubuntu` em TODO container criado do molde, e o build seguinte falha no `adb pull` por
# não conseguir sobrescrevê-lo. Molde tem que sair limpo do que foi só andaime de construção.
say "6/9 limpando o andaime da construção (APKs em /tmp) e desligando o Android..."
docker exec -u root "$TMP" sh -lc 'rm -f /tmp/split-*.apk /tmp/gg' >/dev/null 2>&1

# ⚠️ Desligar antes de commitar (ver nota do passo 7).
docker exec "$TMP" adb shell sync >/dev/null 2>&1
docker exec "$TMP" adb emu kill >/dev/null 2>&1
for i in $(seq 1 18); do
  sleep 5
  docker exec "$TMP" sh -lc 'pgrep -f qemu-system >/dev/null' 2>/dev/null || { say "    qemu encerrou em ~$((i * 5))s."; break; }
done
docker stop -t 60 "$TMP" >/dev/null 2>&1

say "7/9 commitando o molde (container PARADO) -> $GOLDEN"
docker commit "$TMP" "$GOLDEN" >/dev/null || die "docker commit falhou."
docker rm -f "$TMP" >/dev/null 2>&1

# ── PASSO 8: PROVAR que o molde presta ────────────────────────────────────────────────────────────
# Um molde quebrado só apareceria no pior momento: depois de apagar um aparelho bom. Custa ~2 min de
# boot verificar agora e evita descobrir com o emulador de produção já destruído.
say "8/9 verificando o molde (sobe um container dele e confere)..."
VERIFY=mtrx-dandroid-verify

# ⚠️ REPROVAR TEM QUE DESTAGAR. Se a verificação falhasse deixando a tag `$GOLDEN` de pé, o molde
# quebrado ficaria pronto pra uso: o `limpar-emulador-a.sh` só checa se a tag EXISTE, então ele
# implantaria a imagem ruim no primeiro chip que queimasse — apagando um aparelho bom pra colocar um
# aparelho defeituoso, no pior momento possível. Aqui a imagem reprovada vira `golden-REJEITADO-<stamp>`
# (guardada pra diagnóstico, invisível pro fluxo) e a tag boa some.
reprova() {
  docker rm -f "$VERIFY" >/dev/null 2>&1
  docker tag "$GOLDEN" "mtrx-android:golden-REJEITADO-$STAMP" >/dev/null 2>&1
  docker rmi "$GOLDEN" >/dev/null 2>&1
  say "molde REPROVADO e destagueado (ficou como mtrx-android:golden-REJEITADO-$STAMP pra diagnóstico)."
  die "$1"
}

run_emulator "$VERIFY" "$GOLDEN" || reprova "não subiu o container de verificação."
wait_boot "$VERIFY" 30 || reprova "o molde NÃO bootou. Investigue antes de limpar qualquer coisa."

wa=$(docker exec "$VERIFY" adb shell "pm list packages | grep whatsapp" 2>/dev/null | tr -d '\r')
jid=$(docker exec "$VERIFY" adb shell \
        "grep -ah -E 'registration_jid|saved_user_before_logout' /data/data/com.whatsapp/shared_prefs/*.xml 2>/dev/null" 2>/dev/null)
# Confere no PRÓPRIO molde o locale EFETIVO (am get-config), não o prop: o prop pode estar certo e o
# framework rodando em inglês — foi exatamente essa a armadilha de 25/07.
vloc=$(docker exec "$VERIFY" adb shell "am get-config" 2>/dev/null | head -1 | grep -o 'pt-rBR' || echo "outro")
[ "$vloc" = "pt-rBR" ] && vloc=pt-BR

[ -n "$wa" ] || reprova "o molde bootou mas está SEM o WhatsApp — é o bug do commit-com-emulador-vivo."
[ -z "$jid" ] || reprova "o molde tem RASTRO de conta ($jid). Um molde precisa nunca ter registrado."
docker rm -f "$VERIFY" >/dev/null 2>&1
[ "$vloc" = "pt-BR" ] \
  && say "    ✅ molde verificado: boota, WhatsApp presente, SEM rastro de conta, locale pt-BR." \
  || say "    ✅ molde verificado (boota, WhatsApp, sem rastro) — ⚠️ mas locale veio '$vloc', não pt-BR: a tela de boas-vindas vai aparecer em inglês."

# `:live` é a tag de TRABALHO (o compose aponta pra ela). Se ainda não existe, nasce do molde.
if ! docker image inspect "$LIVE" >/dev/null 2>&1; then
  say "9/9 criando a tag de trabalho $LIVE a partir do molde"
  docker tag "$GOLDEN" "$LIVE"
else
  say "9/9 $LIVE já existe (estado de trabalho atual) — preservada."
fi

# PODA das tags de backup: cada execução cria uma, e em poucas rodadas viram uma parede de nomes que
# esconde qual é o backup que importa. Custo de storage é zero quando apontam pra a mesma imagem (tag é
# só nome), mas o ruído é real e atrapalha justo quem está diagnosticando. Mantém as 2 mais recentes;
# `docker rmi` numa tag só remove o NOME enquanto houver outras apontando pra imagem.
docker images --format '{{.Tag}}' --filter "reference=mtrx-android" 2>/dev/null \
  | grep '^backup-[0-9]' | sort -r | tail -n +3 \
  | while read -r t; do docker rmi "mtrx-android:$t" >/dev/null 2>&1 && say "poda: tag antiga mtrx-android:$t removida"; done

cat <<EOF

[ouro] ✅ MOLDE PRONTO: $GOLDEN (Android + Google + WhatsApp, ZERO registros)
[ouro]    backup da imagem antiga: mtrx-android:backup-$STAMP

   Daqui pra frente:
     • chip morreu  ->  bash deploy/limpar-emulador-a.sh      (aparelho novo, do molde)
     • chip registrado e funcionando  ->  bash deploy/salvar-estado-emulador-a.sh   (salva em $LIVE)

   O molde NUNCA é usado direto e NUNCA recebe commit. Trabalha-se sempre em cópias.
EOF
