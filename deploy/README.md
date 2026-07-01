# Deploy — 10 ambientes em produção (modelo B: Android KVM + WAHA)

Sobe os 10 ambientes (A..J) num **servidor Linux com `/dev/kvm`**, atrás de **HTTPS**, com
**segredos próprios**, **proxy por chip** e o **Android em container** como aparelho principal
(o WAHA pareia por QR e faz o disparo). Não toca nos composes de dev — tudo aqui é **override**.

> Pré-leitura: [`../docs/architecture.md`](../docs/architecture.md) (regra 1:1:1:1),
> [`../docs/scaling.md`](../docs/scaling.md) (dimensionamento),
> [`../docs/phone-primary-server.md`](../docs/phone-primary-server.md) (SIM gateway),
> [`../docs/proxy.md`](../docs/proxy.md) (proxies).

## O que tem aqui

| Arquivo | Papel |
|---|---|
| `.env.prod.example` | Modelo de todas as variáveis dos 10 stacks (segredos, domínio, proxies, APK). |
| `gen-secrets.sh` | Gera `.env.prod` com segredos aleatórios + senha/hash do noVNC. |
| `gen-config.sh` | Gera o `Caddyfile` e a `landing/` de produção a partir de `MTRX_DOMAIN`. |
| `docker-compose.prod.yml` | Override por stack: web mesma-origem, CORS da landing, host-gateway do ledger. |
| `docker-compose.android.yml` | Limites de CPU/RAM do Android (KVM). |
| `docker-compose.seed-a.yml` | Admin do Stack A por env (só o A não expõe isso no compose base). |
| `docker-compose.caddy.yml` | Reverse proxy HTTPS (TLS automático). |
| `up-all-prod.sh` / `down-all-prod.sh` | Sobe / derruba tudo. |

## Modelo de rede (por que é mesma-origem)

Cada ambiente vive sob **um subdomínio** e o Caddy roteia por caminho — **sem CORS**:

```
app.<domínio>            → landing (escolha de ambiente)
a.<domínio>              → SPA do ambiente A;  /api,/webhooks,/swagger,/health,/sair → api A
phone-a.<domínio>        → tela noVNC do Android A (basic-auth)
… (b..j idem)
```

A única chamada cross-origin é a **landing → cada api** (`/api/presence`), liberada via
`Web__Origins__0=https://app.<domínio>`.

## Pré-requisitos

1. **Servidor dedicado Linux com KVM** (`ls -l /dev/kvm` tem que existir). VPS comum não serve —
   ver `scaling.md`. Sugestão: Hetzner **AX102** (16c/128GB) p/ os 10 com `docker-android`.
2. **Docker + Docker Compose v2**.
3. **DNS** apontando pro IP do servidor (idealmente wildcard):
   `app.<domínio>`, `*.<domínio>` (cobre `a..j` e `phone-a..j`). Portas **80/443** abertas no firewall.
4. **APK do WhatsApp** hospedado numa URL acessível (vai em `PHONE_WA_APK_URL_N`).
5. **Proxies** residencial/móvel BR, 1 por chip (`docs/proxy.md`) — pode ligar depois.
6. **SIM gateway** com os 10 chips (recebe o SMS de registro por API) — `phone-primary-server.md`.

## Passo a passo

```bash
# no servidor, na raiz do repo
bash deploy/gen-secrets.sh          # cria deploy/.env.prod + senha do noVNC (ANOTE)
nano deploy/.env.prod               # preencha MTRX_DOMAIN, MTRX_TLS_EMAIL, PHONE_WA_APK_URL_*,
                                    # e os WAHA_PROXY_* se já tiver os proxies
bash deploy/up-all-prod.sh          # sobe ledger → 10 stacks → Android escalonado → Caddy
```

Primeira execução compila as imagens dos 10 (vários minutos). Depois é cache.

### Registrar cada chip (1× por ambiente)

Na aba **Celular** de cada ambiente (`https://<letra>.<domínio>`):

1. **Provisionar → Ligar → Mostrar tela** (noVNC; pede a senha do `gen-secrets.sh`).
2. **Instalar WhatsApp** (usa o `PHONE_WA_APK_URL_N`) → registrar o número.
3. O **SMS chega no SIM gateway** → leia o código (API/painel do gateway) → digite na tela.
   → o Android vira o **principal**.
4. **Vincular o WAHA** por QR (aba Celular mostra o QR) → o disparo passa a sair pelo WAHA.
5. Repita nos 10. A regra **1:1:1:1** garante que um ban não derruba os outros.

### Ligar os proxies (quando contratar)

Preencha `WAHA_PROXY_N(_USER/_PASS)` no `.env.prod`, valide e recrie:

```bash
pwsh deploy/../scripts/check-proxy-env.ps1   # (ou rode em qualquer máquina) confere pares completos
bash deploy/up-all-prod.sh                   # idempotente: recria com o proxy aplicado
```

**Teste o opt-out** logo após ligar um proxy: mande um "SAIR" e confirme que o contato fica "Saiu"
(o bypass `NO_PROXY=api` já vem nos composes — ver `docs/proxy.md`).

## Operação

```bash
bash deploy/down-all-prod.sh        # derruba tudo (preserva volumes/dados)
docker compose -f docker-compose-3.yml logs -f api      # logs do ambiente C
docker compose -f deploy/docker-compose.caddy.yml logs -f   # logs do Caddy/TLS
```

Trocou de domínio? Reedite `.env.prod` e rode `bash deploy/gen-config.sh` + recrie o Caddy.

## Lacunas conhecidas (honestas — antes de operar em escala)

- **Digital por emulador (IMEI/Android ID) não é automatizada** — `scaling.md`/`recovery.md`.
  Isolar container/porta/volume não basta contra ban correlacionado. Valide 1 chip antes de escalar.
- **Registro em emulador = ban alto** — use chips descartáveis; espere perdas (`phone-primary-server.md`).
- **`ISmsGateway` não está codado** — o OTP de registro/re-verificação é **manual** (você lê o SMS no
  gateway e digita). A automação é um plugue a ligar quando você escolher o modelo do gateway.
- **Backup** — os volumes (`pgdata*`, `waha-sessions*`, `android-data*`) não têm rotina de backup aqui.
- **noVNC** fica atrás de basic-auth no Caddy; o painel do WAHA usa as credenciais `WAHA*_DASH_*`.

## Segurança

- `.env.prod`, `Caddyfile` e `landing/` gerados ficam **fora do git** (ver `.gitignore`).
- Os segredos são **únicos por stack** (gerados). Não reuse os defaults de dev. O `up-all-prod.sh`
  **recusa subir** se sobrar `__GEN__` ou se JWT/PG/WAHA/hook ainda estiverem no valor de dev.
- **Portas em loopback**: Postgres, WAHA, API e noVNC publicam em `127.0.0.1` (`MTRX_BIND`), então
  **nada é acessível fora do servidor** a não ser via Caddy (HTTPS). Não troque `MTRX_BIND` em prod.
- **Webhook autenticado**: com `WAHA_HOOK_TOKEN` setado, o app grava o header `X-Webhook-Token` no
  config da sessão (o WAHA passa a assiná-lo) e o endpoint **rejeita inbound sem token em produção**.
- Volume `caddy-data` guarda os **certificados** — não apague (rate limit do Let's Encrypt).
