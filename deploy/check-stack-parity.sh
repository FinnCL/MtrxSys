#!/usr/bin/env bash
# DRIFT-CHECKER: garante que os stacks 2..10 têm TUDO que o ambiente A (base) tem.
# Compara, pra cada stack, o conjunto de SERVIÇOS e de CHAVES DE CONFIG (Xxx__Yyy do .NET) contra o A —
# valores por-stack (portas, nomes, segredos) NÃO entram na conta, só a ESTRUTURA. Acusa o que falta.
#
# Rode no servidor (precisa de docker + do .env.prod) depois de qualquer mudança nos composes:
#   bash deploy/check-stack-parity.sh
# Saída limpa (sem "FALTA ...") = os 9 estão em paridade de features com o A.
#
# Compara A = base + prod (as features/serviços do A) contra
#          N = docker-compose-N.yml + prod + common-features + emulator-dispatch (o que os 9 recebem).
# Deixa de fora os A-específicos de propósito (seed-a/redroid-a) e os ledgers (padrão compartilhado).
set -uo pipefail
cd "$(dirname "$0")/.."   # raiz do repo

ENV_FILE=deploy/.env.prod
EF=(); [ -f "$ENV_FILE" ] && EF=(--env-file "$ENV_FILE")

# Vars genéricas que os overlays usam — setadas com valores VÁLIDOS só pra o `config` renderizar as
# CHAVES (comparamos chaves, não valores). ATENÇÃO: container_name exige >=2 chars no padrão
# [a-zA-Z0-9][a-zA-Z0-9_.-]+ — um dummy de 1 char (ex.: "x") faz o render INTEIRO falhar (vazio) e o
# checker daria falso-positivo "tudo faltando". Use dummies válidos.
export SEARXNG_CONTAINER=dummy-searxng PHONE_CONTAINER=dummy-android PHONE_ENGINE=docker-android \
       PHONE_ADB_PORT=5555 EMULATOR_HEALTH_DIR=/tmp/x EMULATOR_EGRESS_HEALTH_PATH=/tmp/x/p

# Serviços e chaves __ de um conjunto de arquivos compose renderizado (`config`). Comparamos ESTRUTURA
# (nomes de serviço + nomes de chave), nunca valores — que variam por-stack de propósito.
svc_of()  { docker compose "${EF[@]}" "$@" config --services 2>/dev/null | sort -u; }
keys_of() { docker compose "${EF[@]}" "$@" config 2>/dev/null | grep -oE '[A-Za-z]+(__[A-Za-z0-9]+)+' | sort -u; }

A_FILES=(-f docker-compose.yml -f deploy/docker-compose.prod.yml)
svc_of "${A_FILES[@]}" > /tmp/A.svc
keys_of "${A_FILES[@]}" > /tmp/A.keys

# Guard anti-falso-OK: se o A renderizou VAZIO, o docker/config falhou — comparar vazio com vazio diria
# "OK" enganosamente. Aborta com mensagem clara em vez de dar um verde falso.
if [ ! -s /tmp/A.svc ]; then
  echo "ERRO: não consegui renderizar o A (docker compose config vazio). Rode no servidor, com docker + $ENV_FILE." >&2
  exit 2
fi

problems=0
for n in 2 3 4 5 6 7 8 9 10; do
  N_FILES=(-f "docker-compose-${n}.yml" -f deploy/docker-compose.prod.yml \
           -f deploy/docker-compose.common-features.yml -f deploy/docker-compose.emulator-dispatch.yml)
  svc_of "${N_FILES[@]}" > /tmp/N.svc
  keys_of "${N_FILES[@]}" > /tmp/N.keys

  # Allowlist de serviços que NÃO precisam ser uniformes (não são "features", são scaffolding):
  #   scrcpy               — experimento de espelhamento ABANDONADO (bug de foco), só no A.
  #   cli                  — container utilitário (roda o `mtrx` CLI sob demanda), não é serviço de prod.
  #   waha-emulator-build  — builda a imagem do WAHA STUB; em prod os stacks usam o WAHA real (dispensável).
  #   android              — serviço estático do emulador (inerte: em prod o Android é provisionado dinâmico).
  #   redis                — o A NÃO tem e nada usa; nos 9 fica desligado (profiles) — não é drift.
  #   landing              — extra em alguns stacks; em prod a landing é servida pelo Caddy (gen-config).
  # searxng NÃO está aqui de propósito: é feature (motor do Coletor) e TEM que estar nos 9.
  ALLOW='^(scrcpy|cli|waha-emulator-build|android|redis|landing)$'
  # Chaves ignoradas: Emulator__* é config do WAHA STUB (dev). O A defaulta pro stub (WAHA_IMAGE sobrescreve
  # pro real em prod); os stacks 3-10 já hardcodam o WAHA real e não têm essas chaves. Inerte em prod.
  KEY_ALLOW='^Emulator__'
  miss_svc=$(comm -23 /tmp/A.svc /tmp/N.svc | grep -vE "$ALLOW" || true)
  miss_key=$(comm -23 /tmp/A.keys /tmp/N.keys | grep -vE "$KEY_ALLOW" || true)
  extra_svc=$(comm -13 /tmp/A.svc /tmp/N.svc | grep -vE "$ALLOW" || true)

  if [ -n "$miss_svc$miss_key$extra_svc" ]; then
    problems=$((problems+1))
    echo "── stack ${n}: DRIFT ──────────────────────────────────────────"
    [ -n "$miss_svc" ] && { echo "  FALTA serviço (o A tem):"; echo "$miss_svc" | sed 's/^/    - /'; }
    [ -n "$miss_key" ] && { echo "  FALTA config (o A tem):";  echo "$miss_key" | sed 's/^/    - /'; }
    [ -n "$extra_svc" ] && { echo "  EXTRA serviço (o A não tem):"; echo "$extra_svc" | sed 's/^/    + /'; }
  else
    echo "stack ${n}: OK (paridade de serviços + config com o A)"
  fi
done

echo
if [ "$problems" -eq 0 ]; then echo "✓ Todos os 9 em paridade com o ambiente A."; else
  echo "✗ ${problems} stack(s) com drift — veja acima."; exit 1
fi
