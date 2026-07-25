#!/usr/bin/env bash
# Rebuild LIMPO do emulador do stack A (mtrx-dandroid): apaga o container e recria do zero pela imagem,
# resetando o estado — inclusive o gfxstream corrompido que causa o crash-loop de renderização (GL 0x502
# no glAttachShader/resize, qemu <defunct>, d_wm exit 1). Provado 2026-07-24: rebuild -> qemu vivo + booted.
#
# ⚠️ PERDE o WhatsApp/chip logado (o A é commit-to-image, SEM volume) -> re-registrar depois (físico->
#    emulador). Só faz sentido quando o emulador já está inutilizável (crashando). Ver docs/rebuild-emulator-a.md.
# ⚠️ SÓ pro stack A (compose-managed). B-J são docker-run (provisionados pela aba) -> NÃO use aqui.
set -u

CONTAINER=mtrx-dandroid
PROJECT=mtrxsys
COMPOSE=deploy/docker-compose.emulator-a.yml

# roda a partir da raiz do repo (o -f é relativo a ela)
cd "$(dirname "$0")/.." || { echo "[rebuild-A] não achei a raiz do repo"; exit 1; }
[ -f "$COMPOSE" ] || { echo "[rebuild-A] $COMPOSE não encontrado — rode da raiz do MtrxSys"; exit 1; }

# MIGRAÇÃO (25/07): o compose passou a apontar pra `mtrx-android:live` (tag de trabalho) em vez da
# upstream sobrescrita. Se `:live` ainda não existe — servidor que não rodou a migração —, criamos a
# partir da imagem que o compose usava antes. Isso PRESERVA o comportamento atual (recria do estado
# acumulado); quem quer aparelho SEM histórico usa deploy/limpar-emulador-a.sh, que parte do molde.
LIVE=mtrx-android:live
if ! docker image inspect "$LIVE" >/dev/null 2>&1; then
  echo "[rebuild-A] $LIVE não existe — migrando da imagem antiga (preserva o estado atual)..."
  docker tag budtmo/docker-android:emulator_14.0 "$LIVE" 2>/dev/null || {
    echo "[rebuild-A] não achei imagem pra migrar. Rode: bash deploy/build-golden-image-a.sh"; exit 1; }
fi

echo "[rebuild-A] apagando o container corrompido ($CONTAINER)..."
docker rm -f "$CONTAINER" >/dev/null 2>&1

echo "[rebuild-A] recriando do zero (projeto $PROJECT, $COMPOSE)..."
docker compose -p "$PROJECT" -f "$COMPOSE" up -d dandroid || {
  echo "[rebuild-A] falha ao subir pelo compose — cheque o docker/compose no host"; exit 1; }

echo "[rebuild-A] aguardando o boot (cold boot leva ~2-3 min)..."
for i in $(seq 1 20); do
  sleep 10
  b=$(docker exec "$CONTAINER" adb -s emulator-5554 shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
  if [ "$b" = "1" ]; then
    echo "[rebuild-A] BOOTED OK em ~$((i * 10))s. Deixe o watchdog montar o proxy (~20s) e confirme o"
    echo "           badge 'Proxy do emulador: OK'. NÃO rode adb pesado em cima (churn re-degrada)."
    exit 0
  fi
done

echo "[rebuild-A] ainda não bootou em 200s. Diagnostique:"
echo "           docker logs --tail 30 $CONTAINER"
echo "           docker exec $CONTAINER sh -lc 'tail -20 /home/androidusr/logs/device.stdout.log'"
exit 1
