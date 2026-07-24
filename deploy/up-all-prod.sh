#!/usr/bin/env bash
# Sobe os 10 ambientes em PRODUÇÃO (modelo B: Android KVM + WAHA), com:
#   1) banco compartilhado (ledger)
#   2) base dos 10 stacks (sem o Android ainda) — derivando as URLs de MTRX_DOMAIN
#   3) Android (KVM): provisionado SOB DEMANDA pela aba "Celular" (não sobe aqui)
#   4) Caddy (HTTPS)
# Rode da RAIZ do repo:  bash deploy/up-all-prod.sh
set -euo pipefail
cd "$(dirname "$0")/.."   # raiz do repo

ENV_FILE=deploy/.env.prod
[ -f "$ENV_FILE" ] || { echo "ERRO: $ENV_FILE não existe. Rode deploy/gen-secrets.sh."; exit 1; }
[ -e /dev/kvm ]   || { echo "ERRO: /dev/kvm ausente. O modelo B exige host Linux com KVM."; exit 1; }
# Os segredos vão pro compose via --env-file (literal). O script só precisa de
# alguns valores pra sua própria lógica — lidos sem expandir (não dar 'source'
# no arquivo: o hash bcrypt tem '$' e quebraria).
# `|| true`: sob `set -euo pipefail`, um grep sem match retorna 1 e derrubaria a atribuição
# `x=$(getenv …)` ANTES das checagens — o guard precisa ver o valor vazio, não abortar cru.
getenv() { grep -E "^$1=" "$ENV_FILE" | head -1 | cut -d= -f2- || true; }
MTRX_DOMAIN=$(getenv MTRX_DOMAIN)
[ -n "$MTRX_DOMAIN" ] || { echo "ERRO: defina MTRX_DOMAIN no $ENV_FILE"; exit 1; }
# Domínio (da marca) SÓ pro link de opt-out — mascara o nome do sistema pro destinatário. Opcional:
# vazio = link continua saindo em <letra>.<MTRX_DOMAIN>. Precisa casar com gen-config.sh (o Caddy só
# atende <letra>.<MTRX_OPTOUT_DOMAIN> se este estiver setado lá também) + DNS de <a..j> apontado pro servidor.
MTRX_OPTOUT_DOMAIN=$(getenv MTRX_OPTOUT_DOMAIN)
# Quais stacks têm EMULADOR (tela na UI + disparo pela UI + proxy in-guest + gate de egresso). Lista de
# números separados por espaço; default = TODOS (paridade com o A: os 10 mostram a tela do emulador). A
# tela fica em branco até o chip ser provisionado, e o gate fail-closed não deixa disparar sem emulador
# real — então "todos" é seguro. Pra DESLIGAR num stack específico: EMULATOR_STACKS="1 2 3" no .env.prod.
EMULATOR_STACKS=$(getenv EMULATOR_STACKS); EMULATOR_STACKS=${EMULATOR_STACKS:-"1 2 3 4 5 6 7 8 9 10"}
emulator_on() { case " $EMULATOR_STACKS " in *" $1 "*) return 0 ;; *) return 1 ;; esac; }

# ── Guard de segredos (fail-closed) ──────────────────────────────────────────
# Os composes têm defaults FRACOS pra dev (PG=mtrx, WAHA dash admin/admin, api-key
# mtrxsys-dev-key, JWT dev-only-...). Em produção eles são sobrescritos pelo .env.prod;
# se um segredo faltar ou continuar com o marcador __GEN__/valor de dev, o stack subiria
# inseguro EM SILÊNCIO. Aqui recusamos subir até estarem preenchidos com segredo real.
guard_secrets() {
  local bad=0
  # 1) Nenhum __GEN__ pode sobrar (gen-secrets.sh deveria ter trocado todos).
  if grep -q '__GEN__' "$ENV_FILE"; then
    echo "ERRO: ainda há '__GEN__' no $ENV_FILE — rode deploy/gen-secrets.sh antes de subir."
    bad=1
  fi
  # 2) Segredos por-stack VAZIOS (A..J têm vars próprias: PG_PASS, PG2_PASS..PG10_PASS, idem
  #    JWT*_SIGNING_KEY, WAHA*_API_KEY, WAHA*_HOOK_TOKEN). Vazio faz o compose cair no default
  #    fraco de dev — checa o arquivo inteiro, não só o Stack A.
  # SEED*_ADMIN_PASS entra aqui: vazio faz o compose cair no default (admin123! etc.) — e esse
  # login do dashboard é PÚBLICO via Caddy (mesma-origem). __GEN__ cobre o fluxo normal; isto pega
  # a edição manual que esvazia.
  if grep -Eq '^(PG[0-9]*_PASS|JWT[0-9]*_SIGNING_KEY|WAHA[0-9]*_API_KEY|WAHA[0-9]*_HOOK_TOKEN|SEED[0-9]*_ADMIN_PASS)=[[:space:]]*$' "$ENV_FILE"; then
    echo "ERRO: há segredo por-stack VAZIO (PG/JWT/WAHA/HOOK/SEED) no $ENV_FILE — preencha todos os A..J."
    bad=1
  fi
  # 3) Segredos por-stack no VALOR DE DEV conhecido (PG=mtrx, JWT dev-only-*, WAHA api-key
  #    mtrxsys-dev-key*) — abririam o sistema. Cobre A..J de uma vez.
  if grep -Eq '^PG[0-9]*_PASS=mtrx[[:space:]]*$|^JWT[0-9]*_SIGNING_KEY=dev-only-|^WAHA[0-9]*_API_KEY=mtrxsys-dev-key' "$ENV_FILE"; then
    echo "ERRO: há segredo por-stack no VALOR DE DEV (PG=mtrx / JWT dev-only-* / WAHA mtrxsys-dev-key). Gere segredos reais."
    bad=1
  fi
  # 4) PORTÃO de autenticação (MtrxSys.Gate): sem hash de senha OU sem segredo TOTP, o Gate sobe
  #    fail-closed e NINGUÉM entra — e como o Caddy põe TODOS os subdomínios atrás do /authz do
  #    Gate, o sistema ficaria inacessível (502). Recusa subir até o gen-secrets.sh / gen-gate-secrets.sh
  #    terem preenchido. (É o que garante que "Sair" desloga de verdade: o portão precisa existir.)
  if [ -z "$(getenv GATE_PASSWORD_HASH)" ] || [ -z "$(getenv GATE_TOTP_SECRET)" ]; then
    echo "ERRO: GATE_PASSWORD_HASH/GATE_TOTP_SECRET vazios no $ENV_FILE — rode deploy/gen-gate-secrets.sh (gera a senha do portão)."
    bad=1
  fi
  [ "$bad" = "0" ] || { echo "Abortei: preencha o $ENV_FILE (deploy/gen-secrets.sh) e rode de novo."; exit 1; }
  echo "✓ segredos de produção conferidos (sem defaults de dev)."

  # Avisos (não bloqueiam): o dedup/opt-out cross-chip só vale com o ledger em Enforce, e a 5440
  # do banco compartilhado fica em 0.0.0.0 (precisa do gateway) — o operador tem que fechá-la no firewall.
  local mode; mode=$(getenv SHARED_LEDGER_MODE); mode=${mode:-Off}
  [ "$mode" = "Enforce" ] || echo "AVISO: SHARED_LEDGER_MODE=$mode (não Enforce) — dedup/opt-out CROSS-chip não suprime de fato."
  echo "AVISO: proteja a porta 5440 (Postgres compartilhado) no FIREWALL — ela escuta em 0.0.0.0."
  echo "       ex.: ufw deny 5440/tcp   (ou libere só a partir das subredes docker)."
}
guard_secrets

LETTERS=(a b c d e f g h i j)
COMPOSES=(docker-compose.yml docker-compose-2.yml docker-compose-3.yml docker-compose-4.yml \
          docker-compose-5.yml docker-compose-6.yml docker-compose-7.yml docker-compose-8.yml \
          docker-compose-9.yml docker-compose-10.yml)
LEDGERS=(docker-compose.ledger.yml docker-compose-2.ledger.yml docker-compose-3.ledger.yml \
         docker-compose-4.ledger.yml docker-compose-5.ledger.yml docker-compose-6.ledger.yml \
         docker-compose-7.ledger.yml docker-compose-8.ledger.yml docker-compose-9.ledger.yml \
         docker-compose-10.ledger.yml)

export LANDING_ORIGIN="https://app.${MTRX_DOMAIN}"
EF=(--env-file "$ENV_FILE")

echo "== [1/5] banco compartilhado (ledger) =="
docker compose "${EF[@]}" -f docker-compose.shared.yml up -d

echo "== [2/5] base dos 10 ambientes (build) =="
for n in 1 2 3 4 5 6 7 8 9 10; do
  i=$((n-1)); L=${LETTERS[$i]}
  export WEB_PUBLIC_API_URL="https://${L}.${MTRX_DOMAIN}"
  # Tela do emulador na aba Celular + toggle "Com emulador" (que COMANDA o disparo pela UI): só nos
  # stacks com emulador ligado (EMULATOR_STACKS). Mostrá-lo num stack SEM emulador é armadilha: ligar o
  # modo faria o disparo mirar um emulador inexistente e falhar. Vazio nos demais = sem toggle (WahaOnly).
  # vnc_lite.html?scale=true (e NAO a raiz "/"): o cliente LEVE do noVNC nao tem barra de controle e,
  # com scale=true, ESCALA o desktop remoto pra caber no iframe preservando a proporcao. A raiz servia
  # a UI completa em tamanho fixo, e a aba Celular precisava recortar o iframe no CSS (transform +
  # overflow) pra esconder a barra — recorte que tambem comia conteudo do Android (topo/rodape). Com o
  # cliente certo o recorte deixou de existir: a tela cabe inteira e acompanha o tamanho do painel.
  if emulator_on "$n"; then export WEB_EMULATOR_URL="https://phone-${L}.${MTRX_DOMAIN}/vnc_lite.html?scale=true"; else export WEB_EMULATOR_URL=""; fi
  export "PHONE_VIEW_URL_${n}=https://phone-${L}.${MTRX_DOMAIN}"
  # Base do link de opt-out: subdomínio da MARCA quando MTRX_OPTOUT_DOMAIN está setado (mascara o nome
  # do sistema pro destinatário), senão o próprio domínio do stack. O token é por-stack, então cada
  # letra tem que bater na api do seu stack — daí o subdomínio por letra (a.<marca>, b.<marca>…).
  if [ -n "$MTRX_OPTOUT_DOMAIN" ]; then
    export "OPTOUT_PUBLIC_URL_${n}=https://${L}.${MTRX_OPTOUT_DOMAIN}"
  else
    export "OPTOUT_PUBLIC_URL_${n}=https://${L}.${MTRX_DOMAIN}"
  fi
  # Token do webhook por-stack no header X-Webhook-Token (via deploy/docker-compose.hookfix.yml). Sem
  # ele, o webhook GLOBAL do WAHA POSTa os eventos SEM o header e a api recusa 401 (fail-closed) — nada
  # aparece no Chat. Stack 1 = WAHA_HOOK_TOKEN; demais = WAHA<n>_HOOK_TOKEN.
  if [ "$n" = "1" ]; then export STACK_HOOK_TOKEN=$(getenv WAHA_HOOK_TOKEN); else export STACK_HOOK_TOKEN=$(getenv "WAHA${n}_HOOK_TOKEN"); fi
  F=(-f "${COMPOSES[$i]}" -f "${LEDGERS[$i]}" -f deploy/docker-compose.prod.yml -f deploy/docker-compose.hookfix.yml)
  # Stack A: seed + PILOTO redroid (Phone__Engine=redroid). Sem este override, o deploy padrão
  # reverteria o A pra docker-android em silêncio. Some quando o redroid virar o default dos 10.
  [ "$n" = "1" ] && F+=(-f deploy/docker-compose.seed-a.yml -f deploy/docker-compose.redroid-a.yml)
  # O ambiente A (n=1) usa SÓ a base + seed/redroid — NÃO recebe overlay por-stack (fica intocado, com
  # as vars SEM sufixo do .env.prod). Limpa os genéricos pra nenhum valor de outro stack vazar pro A
  # (A é o 1º do laço, mas a limpeza deixa isso robusto independente da ordem).
  if [ "$n" = "1" ]; then
    unset PHONE_CONTAINER PHONE_ENGINE PHONE_ADB_PORT EMULATOR_HEALTH_DIR EMULATOR_EGRESS_HEALTH_PATH \
          ALERT_WEBHOOK_URL ALERT_LABEL COLLECTOR_SEARXNG_URL SERPER_API_KEY SEARXNG_CONTAINER \
          ADDRBOOK_ENABLED ADDRBOOK_PROVIDER ADDRBOOK_GRACE \
          ADDRBOOK_GOOGLE_CLIENT_ID ADDRBOOK_GOOGLE_CLIENT_SECRET ADDRBOOK_GOOGLE_REFRESH_TOKEN
  else
    # ── Features COMUNS que o A tem na base e faltavam nos 9 (Alert/Coletor/Google sync) ──
    # Valores POR-STACK vindos dos sufixos _N do .env.prod (vazio/false por padrão = desligado). O
    # overlay usa estes nomes genéricos; cada n>1 re-exporta os seus antes do `up`, sem vazar.
    export ALERT_WEBHOOK_URL="$(getenv ALERT_WEBHOOK_URL_${n})"
    export ALERT_LABEL="$(getenv ALERT_LABEL_${n})"
    export COLLECTOR_SEARXNG_URL="$(getenv COLLECTOR_SEARXNG_URL_${n})"
    export SERPER_API_KEY="$(getenv SERPER_API_KEY_${n})"
    export ADDRBOOK_ENABLED="$(getenv ADDRBOOK_ENABLED_${n})"
    export ADDRBOOK_PROVIDER="$(getenv ADDRBOOK_PROVIDER_${n})"
    export ADDRBOOK_GRACE="$(getenv ADDRBOOK_GRACE_${n})"
    export ADDRBOOK_GOOGLE_CLIENT_ID="$(getenv ADDRBOOK_GOOGLE_CLIENT_ID_${n})"
    export ADDRBOOK_GOOGLE_CLIENT_SECRET="$(getenv ADDRBOOK_GOOGLE_CLIENT_SECRET_${n})"
    export ADDRBOOK_GOOGLE_REFRESH_TOKEN="$(getenv ADDRBOOK_GOOGLE_REFRESH_TOKEN_${n})"
    # Nome do container do searxng por-stack (o serviço vem do overlay; o Coletor o alcança em searxng:8080).
    export SEARXNG_CONTAINER="mtrx${n}-searxng"
    F+=(-f deploy/docker-compose.common-features.yml)
    # Remove o redis morto que JÁ esteja rodando: o profile 'disabled' impede o `up` de recriá-lo, mas não
    # para o container que já está de pé. Nada depende dele (dead weight) → remoção segura, libera RAM.
    docker rm -f "mtrx${n}-redis" >/dev/null 2>&1 || true
    # ── Modo EMULADOR: dá ao DISPATCHER o que o A tem na base (docker.sock + Phone__* + gate de egresso).
    # Genéricos com o MESMO valor que a api do stack usa (PHONE_CONTAINER_${n} etc.) → dispatcher e api
    # miram o MESMO container. Cria a pasta de saúde (o watchdog@${n} escreve) e liga o gate. Aplicado a
    # todo stack em EMULATOR_STACKS (default = todos). ──
    if emulator_on "$n"; then
      pc=$(getenv "PHONE_CONTAINER_${n}"); export PHONE_CONTAINER="${pc:-mtrx${n}-android}"
      pe=$(getenv "PHONE_ENGINE_${n}");    export PHONE_ENGINE="${pe:-docker-android}"
      # AdbPort só é usado pelo engine redroid (docker-android fala adb DENTRO do container); ainda assim
      # o default segue a convenção única-por-stack (A=5555…J=5564) pra dois stacks redroid não colidirem.
      pa=$(getenv "PHONE_ADB_PORT_${n}");  export PHONE_ADB_PORT="${pa:-$((5554 + n))}"
      export EMULATOR_HEALTH_DIR="/home/ubuntu/emulator-health-${n}"
      mkdir -p "$EMULATOR_HEALTH_DIR" 2>/dev/null || true
      export EMULATOR_EGRESS_HEALTH_PATH="/proxy-health/proxy.health"
      F+=(-f deploy/docker-compose.emulator-dispatch.yml)
    else
      unset PHONE_CONTAINER PHONE_ENGINE PHONE_ADB_PORT EMULATOR_HEALTH_DIR EMULATOR_EGRESS_HEALTH_PATH
    fi
  fi
  echo "   -- ambiente ${L} --"
  docker compose "${EF[@]}" "${F[@]}" up -d --build
done

echo "== [3/5] Android (KVM) — provisionamento SOB DEMANDA (pela aba \"Celular\") =="
# Os aparelhos virtuais NÃO sobem aqui. Cada ambiente provisiona o SEU Android
# (container/porta/volume próprios) quando você registra o chip, pela aba "Celular":
# a API cria o container via docker.sock (DockerCliPhoneOrchestrator). O caminho
# dinâmico funciona nos 10 (só A/B têm o serviço `android` estático, hoje inerte) e
# casa com "validar 1 chip antes de escalar" — não sobe 10 emuladores ociosos de uma vez.
echo "   provisione cada Android na aba \"Celular\" ao registrar o chip (ver deploy/README.md)."

echo "== [4/5] portão de autenticação (Gate) =="
# TEM que subir ANTES do Caddy: o Caddyfile gerado põe app./a..j/phone-*/hml atrás do
# forward_auth → 127.0.0.1:8099. Se o Gate não estiver no ar, o Caddy dá 502 em tudo; se
# NÃO subíssemos o Gate aqui, o login ficaria na mão do operador (o bug que deixava a landing
# pública, sem pedir nada). Aqui ele é parte do deploy e é verificado.
docker compose "${EF[@]}" -f deploy/docker-compose.gate.yml up -d --build
echo "   aguardando o Gate responder em 127.0.0.1:8099/healthz ..."
probe() { curl -fsS "$1" >/dev/null 2>&1 || wget -q -O /dev/null "$1" 2>/dev/null; }
gate_ok=0
for _ in $(seq 1 30); do
  if probe http://127.0.0.1:8099/healthz; then gate_ok=1; break; fi
  sleep 1
done
[ "$gate_ok" = "1" ] || {
  echo "ERRO: o Gate não respondeu em 127.0.0.1:8099/healthz — abortando ANTES de subir o Caddy"
  echo "      (senão o Caddy poria os 10 ambientes atrás de um portão morto = 502 em tudo)."
  echo "      Veja os logs: docker logs mtrx-gate"
  exit 1
}
echo "   ✓ Gate no ar."

echo "== [5/5] Caddy (HTTPS) =="
bash deploy/gen-config.sh
docker compose -f deploy/docker-compose.caddy.yml up -d
# RELOAD explícito: o `up -d` acima NÃO recarrega o Caddy quando só o CONTEÚDO do Caddyfile muda — o
# arquivo é bind mount (:ro) e o Compose não vê mudança no serviço, então não recria o container, e o
# Caddy só relê o config no boot ou num reload. Sem isto, toda mudança no Caddyfile gerado (ex.: a rota
# /s/* do opt-out) fica no disco mas NUNCA entra em vigor. O reload é gracioso (zero downtime) e VALIDA
# o config: Caddyfile com erro → reload falha → o Caddy mantém o anterior e o set -e aborta o deploy
# loud (fail-closed, melhor que aplicar config quebrado). Se o container acabou de subir no `up -d`, o
# reload relê o mesmo arquivo — inofensivo.
docker exec mtrx-caddy caddy reload --config /etc/caddy/Caddyfile --adapter caddyfile

cat <<EOF

✓ No ar (tudo atrás do portão de autenticação — ver deploy/docker-compose.gate.yml):
  Portão:       https://auth.${MTRX_DOMAIN}   (usuário + senha + 2FA)
  Landing:      https://app.${MTRX_DOMAIN}
  Ambientes:    https://a.${MTRX_DOMAIN} .. https://j.${MTRX_DOMAIN}
  Tela Android: https://phone-a.${MTRX_DOMAIN} ..

⚠️  SEGURANÇA — rode UMA vez com sudo (fecha 5440 e o noVNC da internet):
      sudo bash deploy/setup-firewall.sh

Próximo: em cada ambiente, aba "Celular" → Provisionar/Ligar Android → registrar
o número por SMS (SIM gateway) → vincular o WAHA por QR. Ver deploy/README.md.
EOF
