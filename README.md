# MtrxSys

Plataforma CRM no WhatsApp pra captura de leads via grupos, gestão de pipeline e disparo de campanhas com proteções anti-ban (Spintax, delay, typing simulado, warm-up, circuit breaker).

## Stack

- Backend: .NET 10 (C#), ASP.NET Core Minimal API, Worker Service, EF Core 10 + PostgreSQL 17, JWT Bearer.
- Frontend: React 19 + TypeScript + Vite 8, openapi-fetch.
- Integração WhatsApp: WAHA (devlikeapro/waha) via webhook + REST.
- Mensageria/cache: Redis 7.
- Containers: Docker Compose.

## Arquitetura

DDD + Clean Architecture + SOLID.

```
src/
  MtrxSys.Core/           Domain + Application (sem deps externas)
  MtrxSys.Infrastructure/ EF Core, WAHA client, Auth (BCrypt + JWT)
  MtrxSys.Api/            Minimal API (REST + webhook)
  MtrxSys.Dispatcher/     Worker Service (motor de envio)
  MtrxSys.Cli/            CLI (debug/dev, opcional)
  mtrxsys-web/            React + TS + Vite
tests/
  MtrxSys.Core.UnitTests/
  MtrxSys.Infrastructure.IntegrationTests/
  MtrxSys.Dispatcher.IntegrationTests/
```

## Pré-requisitos

- **Docker Desktop** (ou equivalente com `docker compose` >= v2). Tudo roda em containers.
- ~3 GB livres em disco pras imagens (postgres, redis, waha, .NET runtime, node).
- Portas livres no host: `5080`, `5173`, `3000`, `5432`, `6379`.

## Como rodar (localhost)

O sistema é 100% conteinerizado — sobe via Docker Compose. Dois modos disponíveis:

### Modo produção-like (recomendado pra usar)

```powershell
start.cmd
```

O script:
1. Roda `docker compose up -d --build` (sobe Postgres + Redis + WAHA + Api + Dispatcher + Web)
2. Aguarda a Api ficar `healthy` (timeout 120s)
3. Abre o browser em `http://localhost:5173`

O `web` é servido por nginx com o bundle de produção (build estático do React).

### Modo desenvolvimento (HMR ligado)

```powershell
dev.cmd
```

Igual ao anterior, mas roda com o override `docker-compose.dev.yml`: o serviço `web` usa o **Vite dev server** com bind mount em `src/mtrxsys-web`. Mudou arquivo → browser recarrega em ~1s sem rebuild de container.

### Sem os helpers (manual)

Se preferir rodar direto sem os `.cmd`:

```bash
# Modo prod
docker compose up -d --build

# Modo dev (HMR)
docker compose -f docker-compose.yml -f docker-compose.dev.yml up -d --build
```

### Comandos úteis

```bash
docker compose ps                    # ver status dos containers
docker compose logs -f api           # ver logs de um serviço
docker compose down                  # parar tudo (preserva dados)
docker compose down -v               # parar e apagar volumes (perde pareamento WhatsApp)
docker compose up -d --build api     # rebuildar apenas um serviço
```

### Primeira vez

A primeira execução demora 2-5 minutos (build das 4 imagens custom + pull das oficiais). As próximas usam o cache do Docker e sobem em ~10 segundos.

Migrations do banco rodam automaticamente no boot da Api. Usuário admin (`admin@local` / `admin123!`) é criado no primeiro boot se a tabela `users` estiver vazia.

## URLs

- Web: http://localhost:5173 — UI principal
- Api: http://localhost:5080/swagger — endpoints REST
- WAHA dashboard: http://localhost:3000/dashboard (creds não funcionam em WAHA Core; o MtrxSys faz tudo que o dashboard faria)
- Postgres: 5432
- Redis: 6379

## Login padrão

- Email: `admin@local`
- Senha: `admin123!`

Criado automaticamente no primeiro startup se a tabela `users` estiver vazia. JWT dura 7 dias.

## Fluxo de uso

1. **Login** em `http://localhost:5173`
2. **Onboarding WhatsApp** — se a sessão WAHA não estiver `WORKING`, a UI mostra o QR direto na página (rotaciona a cada 20s). Escanear no celular: Aparelhos conectados → Conectar um aparelho.
3. **Chat** — conversas chegam em tempo real via webhook (~5s de polling na UI). Botão "Sincronizar" no topbar puxa o histórico das últimas 50 mensagens por conversa.
4. **Importação de contatos via grupos** — aba "Grupos" lista grupos que você participa; "Importar contatos" cadastra cada participante como `Contact` no CRM, marca com `GroupTag = nome do grupo`.
5. **Pipeline CRM** — abrir conversa, no painel direito: stages (Lead → Qualified → Proposal → Won/Lost), tags customizáveis, notas livres, histórico de mudança de stage.
6. **Campanhas** — aba "Campanhas":
   - Criar template com Spintax: `{Oi|Olá}, {{name|amigo}}, {tudo bem?|td bem?}`
   - Filtrar destinatários: por `stage`, `tag` ou `groupTag` (combinável)
   - Disparar — engine de dispatch agenda envios com delay 45-75s entre eles, typing simulado, warmup gradual, circuit breaker
   - Stats em tempo real: pendentes / enviados / falhas / pulados

## Funcionalidades por área

### CRM

- `Contact` com `Phone E.164`, `Name`, `GroupTag`, `Theme`, `OptInAt`, `OptOutAt`, `LastSentAt`, `Stage` (Lead/Qualified/Proposal/Won/Lost), `StageChangedAt`
- `ContactNote` (notas livres por contato, com `CreatedByUserId`)
- `ContactTag` + `ContactTagAssignment` (tags customizáveis)
- `ContactStageChange` (auditoria de mudanças de stage)
- `Conversation` (chat agrupado por `waChatId`) + `ChatMessage` (idempotente por `WaMessageId`)
- Tratamento correto de `@c.us` (telefone real), `@g.us` (grupo), `@lid` (identificador privado do WhatsApp)

### Disparo

- `SpintaxExpander` — `{a|b|{c|d}}` recursivo, escape `\{ \| \}`, depth 8, output 4 KB max
- `MessageComposer` — Spintax + placeholders `{{name|default}}`, `{{phone}}`, `{{group}}`, `{{theme}}`
- `DelayPolicy` — random uniforme entre `DelayMin/MaxSeconds`
- `TypingSimulator` — typing proporcional ao texto com jitter
- `WarmupManager` — curva [20, 40, 80, 150, 250, 400, 500] envios/dia
- `CircuitBreaker` — para em N falhas consecutivas, abre por X minutos
- `DispatchEngine` — loop: breaker check → warmup check → dequeue → compose → typing → send → audit → delay
- `SendAuditEntry` — log de cada envio (telefone, texto renderizado, timings)

### Auth

- BCrypt para senhas
- JWT Bearer (HMAC SHA-256), TTL 7 dias
- Endpoint público: `/api/auth/login`
- Webhooks `/webhooks/waha` aberto, com token opcional via header `X-Webhook-Token` (validado com `FixedTimeEquals`)
- Demais endpoints exigem `Authorization: Bearer <jwt>`

### Webhooks

- WAHA configurado automaticamente no startup da API (`WahaWebhookEnsurer`) com URL interna `http://api:8080/webhooks/waha`
- Eventos capturados: `message`, `message.any`
- Idempotência via lookup por `WaMessageId` antes de inserir
- Concorrência tolerada via catch específico de `DbUpdateException 23505`

## Configurações importantes (`appsettings.json`)

```json
"Dispatch": {
  "SessionId": "default",
  "DelayMinSeconds": 45,
  "DelayMaxSeconds": 75,
  "TypingMinSeconds": 2,
  "TypingMaxSeconds": 5,
  "TypingJitter": 0.15
},
"CircuitBreaker": { "FailureThreshold": 3, "OpenDurationMinutes": 120 },
"Warmup": { "Curve": [20, 40, 80, 150, 250, 400, 500] },
"Jwt": { "AccessTokenMinutes": 10080 },
"Seed": { "Admin": { "Email": "admin@local", "Password": "admin123!" } }
```

## Variáveis de ambiente

Veja `.env.example` na raiz. As principais são `WAHA_API_KEY`, `JWT_SIGNING_KEY`, `PG_PASS`.

## Notas

- **Localhost only**. Não foi pensado pra produção — sem HTTPS, sem Docker Secrets, sem rate limit.
- **WAHA Core ignora `WAHA_DASHBOARD_*`** — dashboard nativo do WAHA não loga com `admin/admin`. Use o MtrxSys.
- **Privacidade do WhatsApp** — contatos com privacidade de número ativada aparecem como `@lid`; o MtrxSys mostra "Contato privado" em vez de inventar um número.
- **Warmup começa em 20 envios/dia**. Pra liberar mais cedo, ajuste o array `Warmup:Curve` no `appsettings.json`.

## Testes

```
dotnet test tests/MtrxSys.Core.UnitTests
```

Cobertura unitária:
- `SpintaxExpander` (parser, escape, nesting, bounds)
- `WarmupManager` (curva, clamp, threshold)
- `CircuitBreaker` (estados, transições)
- `DelayPolicy` (range, bounds passados ao RNG)
- `MessageComposer` (placeholders, fallback, Spintax + substituição)
- `WebhookIngestionService` (idempotência, filtros, grupos vs 1:1)
- `Result<T>` (smoke tests)
