# MtrxSys

Ferramenta de WhatsApp pra **salvar contatos de grupos** e **disparar em massa** com proteções anti-ban (Spintax, rodízio, delay, typing, warm-up, circuit breaker), **opt-out automático** ("SAIR") e relatório com export pra Excel. Roda **só em localhost** via Docker Compose, com até **10 ambientes/chips em paralelo** (A–J), acessíveis por uma **landing** de seleção.

## Stack

.NET 10 (Minimal API + Worker) · EF Core 10 + PostgreSQL 17 · React 19 + Vite 8 · WAHA · Redis 7 · Docker Compose. DDD + Clean.

```
src/   Core · Infrastructure (EF, WAHA, auth) · Api · Dispatcher (motor de envio) · Cli · mtrxsys-web (React)
tests/ Core.UnitTests · Infrastructure.IntegrationTests · Dispatcher.IntegrationTests
```

## Como rodar

Pré-requisito: **Docker Desktop** (`docker compose` v2+). Cada ambiente é uma stack de 6 containers (inclui um WAHA/Chromium); 10 ambientes ≈ 61 containers e vários GB de RAM — suba só os que usar. 1ª execução leva 2–5 min (builds); depois ~10s (cache).

```powershell
start.cmd     # ambiente A, prod-like → http://localhost:5173
dev.cmd       # ambiente A com HMR completo (front + dotnet watch)

up-all.cmd       # 10 stacks (prod-like) + landing → http://localhost:5175
dev-all-front.cmd # 10 stacks (HMR front) + banco compartilhado + dedup (Observe) — tudo num comando
dev-all-hmr.cmd  # 10 stacks, HMR completo (front + backend) — pesado
rebuild-backend.cmd # recompila api+dispatcher nos 10 (após mexer em C#)
down-all.cmd     # derruba tudo (inclui o banco compartilhado; preserva dados)
```

### Qual modo subir

HMR = editar e ver na hora; o `--build` roda **uma vez** e o código atualiza em tempo real (só deps/Dockerfile pedem novo build). Escolha pelo que vai mexer — **um modo por vez** (`down-all.cmd` antes de trocar):

| Vou desenvolver… | Comando | HMR | Peso |
|---|---|---|---|
| Backend (ou front), 1 chip | `dev.cmd` | front + backend | leve ✅ |
| Front nos 10 | `dev-all-front.cmd` | front + dedup (Observe); C# → `rebuild-backend.cmd` | médio |
| Front + backend nos 10 | `dev-all-hmr.cmd` | front + backend | pesado ⚠️ |

> O código é o **mesmo** nos 10 — pra testar lógica, `dev.cmd` (1 chip) basta. Os 10 servem pra operar 10 chips, não pra desenvolver. Os `.cmd` só orquestram `docker compose -f ...`; pra variações, abra o script.

## URLs e logins

Landing: **http://localhost:5175** (campos já preenchidos). API em `/swagger`. JWT dura 7 dias.

| Amb | Web | API | WAHA | Login |
|-----|-----|-----|------|-------|
| A | 5173 | 5080 | 3000 | `admin@local` / `admin123!` |
| B | 5174 | 5081 | 3001 | `admin-b@local` / `chipB123!` |
| … | … | … | … | `admin-<letra>@local` / `chip<Letra>123!` |
| J | 5183 | 5089 | 3009 | `admin-j@local` / `chipJ123!` |

## Fluxo de uso

UI com 4 abas (após o WhatsApp conectar): **Chat · Grupos · Contatos · Disparo**.

1. **Onboarding** — sem sessão ativa, mostra o QR.
2. **Grupos** → "Importar contatos" salva participantes como `Contact`.
3. **Contatos** — por grupo, com **Reativar** (opt-out) e export pra Excel.
4. **Disparo** — pote de mensagens (Spintax) → público → confirma → iniciar/parar/retomar → relatório + export.
5. **Chat** — conversas via webhook (~10s) + auto-sync 60s.

**Opt-out**: responder "sair" tira o contato dos disparos e envia uma confirmação; o admin desfaz em **Reativar**.

## Anti-ban (motor de envio)

Pote de templates (cada contato sorteia um) · Spintax `{a|b}` + placeholders · rodapé de opt-out na 1ª msg · delay 60–180s + typing simulado · **warm-up** por curva (`[10,15,25,40,50,65,80,100]`, teto **100/dia**, avança por dias de uso) · circuit breaker · pausa manual · guarda de sessão WAHA. **Auto-start** (`Waha:AutoStart`) religa sessão `Stopped` sem QR pro disparo rodar desassistido.

## Configuração

Principais em `src/MtrxSys.Dispatcher/appsettings.json` (a curva que manda é a do Dispatcher):

```json
"Dispatch": { "DelayMinSeconds": 60, "DelayMaxSeconds": 180, "PauseWhenSessionDown": true },
"CircuitBreaker": { "FailureThreshold": 3, "OpenDurationMinutes": 120 },
"Warmup": { "Curve": [10, 15, 25, 40, 50, 65, 80, 100] }, "Waha": { "AutoStart": true }
```

Env vars: ver `.env.example`. No multi-ambiente cada stack N usa sufixo (`PG{N}_PASS`, `WAHA{N}_API_KEY`, `JWT{N}_SIGNING_KEY`, `SEED{N}_ADMIN_*`).

## Registro compartilhado (dedup entre ambientes + opt-out global)

Os 10 bancos são isolados. Um **11º Postgres minúsculo** (`docker-compose.shared.yml`, tabela `phone_ledger`) liga os ambientes pelo **telefone**: se um chip já disparou/registrou opt-out pra um número, os outros **não reenviam** e a UI mostra **"Enviado · outro chip"**. Opcional, **fail-open** (falha nunca trava o disparo).

O `dev-all-front.cmd` já sobe tudo em modo **Observe** (consulta/grava e só **loga** o que faria) — confira os logs `[ledger observe] ... SERIA pulado` e, quando confiar, troque pra `Enforce` na linha `set SHARED_LEDGER_MODE=` no topo do script. `Off` desliga. O `down-all.cmd` derruba o banco compartilhado junto.

Opcional, 1× por chip — carrega o histórico: `POST localhost:5080/api/dispatch/ledger-backfill`. Apps alcançam o banco via `host.docker.internal:5440`.

## Notas

- **Localhost only** — sem HTTPS, Docker Secrets ou rate limit.
- Migrations rodam no boot; admin semeado no 1º boot se `users` vazia.
- **`@lid`** (privacidade): pulado na importação, resolvido pro telefone real em respostas pra não perder opt-out.

## Testes

```
dotnet test tests/MtrxSys.Core.UnitTests
```

Cobre `SpintaxExpander`, `WarmupManager`, `CircuitBreaker`, `DelayPolicy`, `MessageComposer`, `OptOutDetector`, `WebhookIngestionService`.
