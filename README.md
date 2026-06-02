# MtrxSys

Ferramenta de WhatsApp pra **salvar contatos de grupos** e **disparar em massa** com proteções anti-ban (Spintax, rodízio de mensagens, delay, typing simulado, warm-up, circuit breaker), **opt-out automático** ("SAIR"), classificação de quem respondeu e relatório com export pra Excel.

Roda **só em localhost** via Docker Compose. Suporta até **10 ambientes/chips em paralelo** (A–J) — cada um com WhatsApp, banco e aquecimento próprios — acessíveis por uma **landing** de seleção.

## Stack

.NET 10 (Minimal API + Worker Service) · EF Core 10 + PostgreSQL 17 · React 19 + Vite 8 · WAHA (WhatsApp via webhook/REST) · Redis 7 · Docker Compose. Arquitetura DDD + Clean.

```
src/   Core (domínio+app) · Infrastructure (EF, WAHA, auth) · Api (REST+webhook)
       Dispatcher (motor de envio) · Cli · mtrxsys-web (React)
tests/ Core.UnitTests · Infrastructure.IntegrationTests · Dispatcher.IntegrationTests
```

## Como rodar

Pré-requisito: **Docker Desktop** (`docker compose` v2+). Cada ambiente é uma stack completa (6 containers, incluindo um WAHA/Chromium); 10 ambientes ≈ 61 containers e vários GB de RAM — suba só os que usar.

**Ambiente único (A):**

```powershell
start.cmd   # prod-like (nginx + bundle), abre http://localhost:5173
dev.cmd     # HMR (Vite dev server, via docker-compose.dev.yml)
```

> `docker-compose.dev.yml` é um *override*: use sempre com o base (`-f docker-compose.yml -f docker-compose.dev.yml`), nunca sozinho. Containers corretos: `mtrx-api` / `mtrx-web` / `mtrx-dispatcher`.

**Multi-ambiente (A–J):**

```powershell
up-all.cmd    # sobe os 10 stacks + landing, abre http://localhost:5175
dev-all.cmd   # Stack 1 com HMR; demais estáticos
down-all.cmd  # derruba tudo (preserva dados)
```

Cada `-f docker-compose-N.yml` mira um stack (sem `-f` = só o A). Ex.: `docker compose -f docker-compose-2.yml up -d --build api web`.

Migrations rodam no boot; o admin é semeado no 1º boot se `users` estiver vazia. 1ª execução leva 2–5 min (builds); depois ~10s (cache do Docker).

## URLs e logins

Landing: **http://localhost:5175**. Por ambiente (API em `/swagger`; WAHA Core ignora o login do dashboard nativo — use o MtrxSys):

| Amb | Web | API | WAHA | Login |
|-----|-----|-----|------|-------|
| A | 5173 | 5080 | 3000 | `admin@local` / `admin123!` |
| B | 5174 | 5081 | 3001 | `admin-b@local` / `chipB123!` |
| C | 5176 | 5082 | 3002 | `admin-c@local` / `chipC123!` |
| … | … | … | … | `admin-<letra>@local` / `chip<Letra>123!` |
| J | 5183 | 5089 | 3009 | `admin-j@local` / `chipJ123!` |

JWT dura 7 dias. Credenciais B–J configuráveis via `SEED{N}_ADMIN_EMAIL/PASS`. A landing já vem com os campos preenchidos.

## Fluxo de uso

UI com 4 abas (aparecem após o WhatsApp conectar): **Chat · Grupos · Contatos · Disparo**.

1. **Onboarding** — sem sessão `WORKING`, a página mostra o QR (rotaciona ~20s).
2. **Grupos** → "Importar contatos" salva participantes como `Contact` (com `GroupTag`); exclui o próprio número e pula `@lid` sem telefone resolvível.
3. **Contatos** — agrupados por grupo (accordion), com **Reativar** (opt-out) e export pra Excel.
4. **Disparo** — pote de mensagens (cada contato sorteia uma, com Spintax) → público (todos / só quem respondeu) → confirma a contagem → iniciar / parar / retomar → relatório em tempo real + export / "Renovar lista".
5. **Chat** — conversas via webhook (polling ~10s) + auto-sync a cada 60s.

**Funil (automático):** Lead→Novo · Qualified→Respondeu · Proposal→Negociando (manual) · Won→Cliente (manual) · Lost→Descartado (opt-out). **Opt-out**: responder "sair" marca o contato, envia **uma** confirmação e tira dos disparos (resolve `@lid`→telefone via WAHA); o admin desfaz em **Reativar**.

## Anti-ban e motor de envio

- **Rodízio**: pote de templates; cada contato sorteia um (`IRandomSource`).
- **Spintax** `{a|b|{c|d}}` + placeholders `{{name|default}}`, `{{phone}}`, `{{group}}`, `{{theme}}`.
- **Rodapé de opt-out na 1ª msg** (`Dispatch:OptOutFooter`) — só quando `LastSentAt == null` e o texto ainda não cita "sair".
- **Delay** 60–180s · **typing simulado** proporcional ao texto.
- **Warm-up** por curva (`Warmup:Curve` = `[10,15,25,40,50]`, platô em **50/dia**) que avança por **dias de uso real** (não calendário); bônus manual / "Disparar todos" liberam o teto do dia.
- **Circuit breaker** (para em N falhas, abre por X min) · **pausa manual** (`IsManuallyPaused`).
- **Guarda de sessão WAHA** (`Dispatch:PauseWhenSessionDown`) — havendo job, se a sessão estiver `Stopped`/`Failed` o ciclo para (job volta a `Pending`); estados transitórios e erro de leitura não travam.
- Loop do `DispatchEngine`: pausa? → breaker → warmup → dequeue → sessão ok? → compose → typing → send → audit → delay.

## Landing e presença (multi-ambiente)

A landing mostra um card por ambiente em grade 5×2, já com credenciais e o `localhost:porta` que abre (do mapa `ENVS`). Ao entrar, autentica no backend do ambiente e abre o dashboard em nova aba (JWT via fragment, limpo do histórico). O logout volta pra landing quando veio dela (guardado em `sessionStorage`; redirect só pra origem `localhost`).

**Lock "Em uso"** (evita dois operadores no mesmo chip): o dashboard mantém um SSE em `/api/presence/connect`; a landing consulta `/api/presence/status` (2s) e trava o card enquanto `active`. É baseado em **conexão, não heartbeat** — minimizar/congelar a aba não derruba; fechar/crash destrava na hora (com debounce de 2 leituras).

**Selo de status do card** — `GET /api/presence/chip` (anônimo, `{ status, breakerOpen }`, cacheado ~5s). Prioridade:

- **Em uso** — dashboard aberto (trava o login).
- **Chip com falha** — WAHA `FAILED`, ou `WORKING` com o breaker aberto (chip não está disparando). O breaker só conta quando `WORKING`.
- **Pareado** — `WORKING`.
- **Desconectado** — padrão (QR não lido, sessão parada, fora do ar).

**Auto-start** (`Waha:AutoStart`, padrão `true`): o auto-sync (~60s) religa sessão `Stopped` com a auth salva (sem QR), pro disparo rodar desassistido. Não toca `FAILED`. Ambiente nunca pareado sobe pra `ScanQrCode` — desligue a flag pra deixá-lo parado.

> O WAHA não distingue "banido" de "caiu" (ambos viram `FAILED`/`ScanQrCode`); o selo mostra só o estado real da sessão.

## Configuração

`appsettings.json` (principais — a curva de warm-up que manda é a do **Dispatcher**, `src/MtrxSys.Dispatcher/appsettings.json`):

```json
"Dispatch": { "DelayMinSeconds": 60, "DelayMaxSeconds": 180, "OptOutFooter": "...responda SAIR.", "PauseWhenSessionDown": true },
"CircuitBreaker": { "FailureThreshold": 3, "OpenDurationMinutes": 120 },
"Warmup": { "Curve": [10, 15, 25, 40, 50] },
"Waha": { "AutoStart": true },
"Jwt": { "AccessTokenMinutes": 10080 }
```

Variáveis de ambiente: ver `.env.example`. Principais `WAHA_API_KEY`, `JWT_SIGNING_KEY`, `PG_PASS`; no multi-ambiente cada stack N tem sufixo (`PG{N}_PASS`, `WAHA{N}_API_KEY`, `JWT{N}_SIGNING_KEY`, `SEED{N}_ADMIN_*`).

## Auth e webhooks

JWT Bearer (HMAC-256, 7 dias) + BCrypt. Públicos: `/api/auth/login` e `/webhooks/waha` (token opcional `X-Webhook-Token`); o resto exige Bearer. Webhook configurado no boot (`WahaWebhookEnsurer`), eventos `message`/`message.any`, idempotente por `WaMessageId`, resolve `@c.us`/`@lid`. Auto-sync de 60s cobre o que o webhook não pega (ex.: entrar num grupo).

## Notas

- **Localhost only** — sem HTTPS, Docker Secrets ou rate limit.
- **`@lid`** (privacidade de número): pulado na importação, mas resolvido pro telefone real em respostas pra não perder opt-out.

## Testes

```
dotnet test tests/MtrxSys.Core.UnitTests
```

Cobre `SpintaxExpander`, `WarmupManager`, `CircuitBreaker`, `DelayPolicy`, `MessageComposer`, `OptOutDetector`, `WebhookIngestionService` e `Result<T>`.
