#!/bin/bash
# Watchdog ROBUSTO do emulador-principal (ambiente A). Roda como systemd (mtrx-emulator-watchdog.service).
# So age em CRASH REAL (stop-after-pair DESLIGADO -> sem sono intencional -> sem churn):
#   - Container EXITED (crashou) -> docker start (o entrypoint limpa os locks e reergue COM a conta).
#   - Container Up mas Android nao booted por 4 checks (~80s, muito > boot normal ~24s) -> docker restart.
# NAO age em: boot em andamento (grace ~80s), adb ocupado no disparo (checa o CONTAINER primeiro), sono.
#
# + CLEANUP DURAVEL DA TELA (aba "Celular"/noVNC): quando o device esta booted, deixa SO o device
#   CENTRALIZADO no molde -> fecha os Extended Controls, esconde a barrinha de ferramenta, e escurece a
#   faixa lateral (a #0d0d0d, cor do .phone-device) pra virar "bezel". Idempotente (roda a cada ciclo);
#   re-provisiona xdotool/xsetroot no container se sumirem. NAO recria o container (a conta da Paulinha
#   vive no LAYER GRAVAVEL -> recreate = perder a conta). SCREEN_WIDTH fica 500; o molde (aspect-ratio
#   500/850) ja bate com o display, entao o conteudo preenche sem letterbox/scroll.
down=0

# Re-provisiona xdotool + xsetroot (e libs) no container se faltarem. So dispara apos um recreate externo
# (no restart normal o layer gravavel persiste). LD_LIBRARY_PATH=/usr/local/lib isola as libs (nao mexe
# no sistema do container).
ensure_tools() {
  if docker exec mtrx-dandroid test -x /usr/local/bin/xdotool 2>/dev/null; then return; fi
  for f in /usr/bin/xdotool /usr/bin/xsetroot; do docker cp "$f" mtrx-dandroid:/usr/local/bin/ >/dev/null 2>&1; done
  docker cp /lib/x86_64-linux-gnu/libxdo.so.3 mtrx-dandroid:/usr/local/lib/ >/dev/null 2>&1
  for lib in $(ldd /usr/bin/xdotool /usr/bin/xsetroot 2>/dev/null | grep -oE '/[^ ]+\.so[^ ]*' | sort -u); do
    docker cp "$lib" mtrx-dandroid:/usr/local/lib/ >/dev/null 2>&1
  done
}

clean_screen() {
  docker exec mtrx-dandroid sh -c 'export DISPLAY=:0 LD_LIBRARY_PATH=/usr/local/lib:/usr/lib:/lib
    xsetroot -solid "#0d0d0d" 2>/dev/null
    xdotool search --name "Extended Controls" windowclose 2>/dev/null
    # a janelinha de ferramenta ("Emulator") — windowunmap NAO segura; joga pra fora da tela (500x850).
    for w in $(xdotool search --name "^Emulator$" 2>/dev/null); do xdotool windowmove "$w" 900 900 2>/dev/null; done
    # forca tamanho (o emulador varia entre boots: 322x680, 401x847...) -> 402x850 preenche a altura;
    # centraliza DINAMICO pela largura do display (404 -> x=1; 500 -> x=49) pra o device caber sem clipar.
    DEV=$(xdotool search --name "Android Emulator" 2>/dev/null | head -1)
    if [ -n "$DEV" ]; then
      xdotool windowsize "$DEV" 402 850 2>/dev/null
      DW=$(xdotool getdisplaygeometry 2>/dev/null | cut -d" " -f1)
      X=$(( (${DW:-404} - 402) / 2 )); [ "$X" -lt 0 ] && X=0
      xdotool windowmove "$DEV" "$X" 1 2>/dev/null
    fi' >/dev/null 2>&1
}

# "desligado DE PROPÓSITO" = existe o volume-flag mtrx-dandroid-off (criado pelo StopAsync / botão
# "Desligar emulador"). Nesse caso o watchdog NÃO religa — só religa em CRASH real. O Start remove a flag.
is_off() { docker volume inspect mtrx-dandroid-off >/dev/null 2>&1; }

while true; do
  status=$(docker ps -a --filter name=mtrx-dandroid --format '{{.Status}}' 2>/dev/null)
  case "$status" in
    Up*)
      B=$(timeout 8 docker exec mtrx-dandroid adb -s emulator-5554 shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
      if [ "$B" = "1" ]; then
        down=0
        ensure_tools
        clean_screen
      else
        down=$((down + 1))
        if [ "$down" -ge 4 ] && ! is_off; then
          docker restart mtrx-dandroid >/dev/null 2>&1
          down=0
          sleep 60
        fi
      fi
      ;;
    "")
      : # container nao existe -> nao e da nossa conta
      ;;
    *)
      # existe mas NAO esta Up (Exited/Created/Dead) -> crashou -> reergue, A MENOS que tenha sido
      # DESLIGADO DE PROPÓSITO (flag mtrx-dandroid-off do botão "Desligar emulador") — aí respeita.
      if ! is_off; then
        docker start mtrx-dandroid >/dev/null 2>&1
        down=0
        sleep 60
      fi
      ;;
  esac
  sleep 20
done
