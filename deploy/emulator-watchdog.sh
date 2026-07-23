#!/bin/bash
# Watchdog ROBUSTO do emulador-principal (ambiente A). Roda como systemd (mtrx-emulator-watchdog.service).
# So age em CRASH REAL (stop-after-pair DESLIGADO -> sem sono intencional -> sem churn):
#   - Container EXITED (crashou) -> docker start (o entrypoint limpa os locks e reergue COM a conta).
#   - Container Up mas Android nao booted por 4 checks (~80s, muito > boot normal ~24s) -> docker restart.
# NAO age em: boot em andamento (grace ~80s), adb ocupado no disparo (checa o CONTAINER primeiro), sono.
#
# + CLEANUP DURAVEL DA TELA (aba "Celular"/noVNC): quando o device esta booted, deixa SO o device
#   CENTRALIZADO no molde -> fecha os Extended Controls, esconde a barrinha de ferramenta, e escurece a
#   faixa lateral (#0d0d0d) pra ela nao aparecer como "desktop" dentro da tela. Idempotente (roda a cada
#   ciclo); ATENCAO: a UI nao desenha mais moldura de celular (o .phone-device virou .phone-screen, sem
#   fundo), entao esse #0d0d0d agora encosta no fundo da pagina (--bg #111b21) em vez de casar com a
#   moldura. Se aparecer emenda de cor na borda, alinhe este solid com o --bg do App.css.
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

# ── PROXY TRANSPARENTE (anti-ban): garante que TODO TCP do emulador sai pelo residencial BR ────────
# POR QUE aqui e não no compose: as regras de iptables vivem na NETNS do container do emulador e
# somem a cada restart — e este watchdog é justamente quem reinicia o emulador em crash. Sem esta
# reaplicação, a primeira queda devolvia o disparo pro IP do datacenter (Montreal) EM SILENCIO. Foi o
# que aconteceu em 18/07: o proxy sumiu e ninguem notou por 5 dias.
#
# POR QUE iptables e nao o http_proxy do Android: aquele e uma SUGESTAO que cada app decide seguir.
# Chrome/Play seguem; o socket de mensagens do WhatsApp (porta 5222) IGNORA e vai direto. O REDIRECT
# desvia o TCP antes de sair, sem o app saber.
#
# nsenter usa o iptables DO HOST dentro da netns do container: nao precisa de NET_ADMIN no emulador
# (habilitar exigiria RECRIAR o container = PERDER A CONTA pareada) nem de subir alpine+apk a cada ciclo.
TP_PORT=12345
ensure_proxy() {
  # Upstream do .env.prod: MESMA fonte do WAHA deste stack, pra os dois nunca divergirem de IP.
  local env=/home/ubuntu/MtrxSys/deploy/.env.prod
  local hostport user pass upstream pid
  hostport=$(grep -E '^WAHA_PROXY_1=' "$env" 2>/dev/null | cut -d= -f2-)
  user=$(grep -E '^WAHA_PROXY_1_USER=' "$env" 2>/dev/null | cut -d= -f2-)
  pass=$(grep -E '^WAHA_PROXY_1_PASS=' "$env" 2>/dev/null | cut -d= -f2-)
  [ -n "$hostport" ] && [ -n "$user" ] || return 0   # sem proxy configurado: nao mexe em nada
  upstream="http://${user}:${pass}@${hostport}"

  pid=$(docker inspect -f '{{.State.Pid}}' mtrx-dandroid 2>/dev/null)
  [ -n "$pid" ] && [ "$pid" != "0" ] || return 0

  # 1) SAUDE POR ALCANCE DA PORTA, nao por "o container existe". Quando o emulador reinicia, a netns
  #    muda e o gost-tp fica ORFAO num laco de restart: `docker ps` ainda mostra o nome, mas nada
  #    responde. Testar a porta DENTRO da netns cobre os tres casos (parado, reiniciando, orfao).
  if ! nsenter -t "$pid" -n timeout 3 bash -c "</dev/tcp/127.0.0.1/${TP_PORT}" 2>/dev/null; then
    docker rm -f mtrx-gost-tp >/dev/null 2>&1
    docker run -d --name mtrx-gost-tp --restart unless-stopped \
      --network "container:mtrx-dandroid" --cap-add NET_ADMIN \
      gogost/gost -L "red://:${TP_PORT}" -F "$upstream" >/dev/null 2>&1
    sleep 4
    # ORDEM CRITICA: so aplica as regras com o proxy RESPONDENDO. Redirecionar pra um gost morto
    # deixaria o emulador SEM REDE NENHUMA — pior que sair pelo IP errado, e mudo no log.
    nsenter -t "$pid" -n timeout 3 bash -c "</dev/tcp/127.0.0.1/${TP_PORT}" 2>/dev/null || return 0
  fi

  # 2) Regras na netns. SENTINELA: as quatro nascem juntas, entao basta checar o REDIRECT — evita 4
  #    nsenter por ciclo no caso comum (tudo certo).
  #    ⚠️ LIMITE CONHECIDO: o REDIRECT em OUTPUT captura o tráfego HTTP e boa parte do egresso, MAS o
  #    socket de mensagens do WhatsApp aberto pelo `netsimd` (rede do docker-android) NÃO cai nesta
  #    cadeia — reconecta DIRETO mesmo com as regras presentes (provado 2026-07-23: após restart, o
  #    socket direto pra 31.13.x:5222 persiste). Matar o socket a cada ciclo só CHURNa o WhatsApp sem
  #    consertar (ele volta direto). O egresso confiável exige TPROXY no bridge do HOST, não aqui —
  #    pendente. Enquanto isso, NÃO confie que 100% do WhatsApp sai pelo residencial só por estas regras.
  nsenter -t "$pid" -n iptables -t nat -C OUTPUT -p tcp -j REDIRECT --to-ports "${TP_PORT}" 2>/dev/null && return 0

  #    Os RETURN vem ANTES do REDIRECT e sao obrigatorios: sem excluir o proprio proxy, o gost
  #    redirecionaria a propria conexao de saida pra si mesmo (laco infinito); sem excluir a rede do
  #    docker e o loopback, o emulador perderia adb/noVNC/api.
  #    Subnet LIDA do docker: fixar "172.19.0.0/16" quebraria silenciosamente se a rede fosse recriada
  #    noutra faixa — o mesmo tipo de armadilha do IP fixo que derrubou o proxy anterior.
  local proxy_ip=${hostport%%:*} subnet
  subnet=$(docker network inspect mtrxsys_default -f '{{range .IPAM.Config}}{{.Subnet}}{{end}}' 2>/dev/null)
  subnet=${subnet:-172.19.0.0/16}
  for r in "-d 127.0.0.0/8 -j RETURN" \
           "-d ${subnet} -j RETURN" \
           "-d ${proxy_ip} -j RETURN" \
           "-j REDIRECT --to-ports ${TP_PORT}"; do
    # shellcheck disable=SC2086
    nsenter -t "$pid" -n iptables -t nat -A OUTPUT -p tcp $r 2>/dev/null
  done
}

# ── PROXY NA ORIGEM (DENTRO do guest) ──────────────────────────────────────────────────────────
# O egresso do WhatsApp NASCE dentro do Android (VM). Interceptar de fora (netns/host) NÃO pega o
# socket de mensagens — o netsimd o entrega já "pronto" e ele sai DIRETO pelo IP do datacenter
# (provado por tcpdump 2026-07-23). A captura confiável é DENTRO do guest, onde o tráfego se origina:
# um gost transparente no Android + iptables nat OUTPUT REDIRECT. Provado: tcpdump no host mostra 0
# direto pra Meta e 100% pelo residencial; fail-closed (gost morto = REDIRECT pra porta morta =
# conexão recusada, não vaza). Requer adb root (build userdebug) — o gost é o MESMO binário do stack.
GUEST_GOST=/home/ubuntu/gost-guest
ensure_guest_proxy() {
  local env=/home/ubuntu/MtrxSys/deploy/.env.prod
  local hostport user pass proxy_ip
  hostport=$(grep -E '^WAHA_PROXY_1=' "$env" 2>/dev/null | cut -d= -f2-)
  user=$(grep -E '^WAHA_PROXY_1_USER=' "$env" 2>/dev/null | cut -d= -f2-)
  pass=$(grep -E '^WAHA_PROXY_1_PASS=' "$env" 2>/dev/null | cut -d= -f2-)
  [ -n "$hostport" ] && [ -n "$user" ] || return 0
  proxy_ip=${hostport%%:*}

  docker exec mtrx-dandroid adb root >/dev/null 2>&1   # idempotente; sobe adbd como root (userdebug)

  # Binário no guest: /data sobrevive a reboot, então re-push só quando some (ex.: recreate/pm clear).
  if ! docker exec mtrx-dandroid adb shell "test -x /data/local/tmp/gost" >/dev/null 2>&1; then
    [ -f "$GUEST_GOST" ] || return 0
    docker cp "$GUEST_GOST" mtrx-dandroid:/tmp/gg >/dev/null 2>&1
    docker exec mtrx-dandroid chmod 644 /tmp/gg 2>/dev/null
    docker exec mtrx-dandroid adb push /tmp/gg /data/local/tmp/gost >/dev/null 2>&1
    docker exec mtrx-dandroid adb shell "chmod 755 /data/local/tmp/gost" >/dev/null 2>&1
  fi

  # Processo do gost (o PROCESSO morre no reboot mesmo com o binário presente): religa se não escuta.
  if ! docker exec mtrx-dandroid adb shell "su 0 ss -ltn 2>/dev/null | grep -q :12345" >/dev/null 2>&1; then
    docker exec mtrx-dandroid adb shell \
      "setsid /data/local/tmp/gost -L red://:12345 -F http://${user}:${pass}@${hostport} >/data/local/tmp/gost.log 2>&1 &" >/dev/null 2>&1
    sleep 2
  fi

  # Regras nat OUTPUT no guest (regras são runtime, somem no reboot). SENTINELA no REDIRECT.
  # RETURN pra loopback / rede do emulador (10.x, inclui DNS 10.0.2.3) / o PROXY (senão o gost
  # redirecionaria a própria saída — laço). O resto do TCP vai pro gost.
  if ! docker exec mtrx-dandroid adb shell "su 0 iptables -t nat -C OUTPUT -p tcp -j REDIRECT --to-ports 12345" >/dev/null 2>&1; then
    docker exec mtrx-dandroid adb shell "su 0 iptables -t nat -A OUTPUT -p tcp -d 127.0.0.0/8 -j RETURN; su 0 iptables -t nat -A OUTPUT -p tcp -d 10.0.0.0/8 -j RETURN; su 0 iptables -t nat -A OUTPUT -p tcp -d ${proxy_ip} -j RETURN; su 0 iptables -t nat -A OUTPUT -p tcp -j REDIRECT --to-ports 12345" >/dev/null 2>&1
    # BOOT-RACE: no boot o WhatsApp conecta DIRETO antes das regras. Recém-aplicadas, um force-stop
    # reseta a conexão dele — que RECONECTA já pelo redirect (dentro do guest o redirect PEGA a
    # reconexão, ao contrário das tentativas de fora). Só roda quando as regras nascem (1x por boot).
    docker exec mtrx-dandroid adb shell "am force-stop com.whatsapp" >/dev/null 2>&1
    docker exec mtrx-dandroid adb shell "monkey -p com.whatsapp 1" >/dev/null 2>&1
  fi
}

while true; do
  status=$(docker ps -a --filter name=mtrx-dandroid --format '{{.Status}}' 2>/dev/null)
  case "$status" in
    Up*)
      B=$(timeout 8 docker exec mtrx-dandroid adb -s emulator-5554 shell getprop sys.boot_completed 2>/dev/null | tr -d '\r')
      if [ "$B" = "1" ]; then
        down=0
        ensure_tools
        ensure_proxy
        ensure_guest_proxy
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
