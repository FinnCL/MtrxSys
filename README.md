# MtrxSys

Ferramenta de WhatsApp pra **salvar contatos de grupos** e **disparar mensagens em massa** com proteções anti-ban (Spintax, rodízio de mensagens, delay, typing simulado, warm-up, circuit breaker). Inclui **opt-out automático** ("SAIR"), classificação automática de quem respondeu e relatório de envios com exportação pra Excel.

> Roda **só em localhost** via Docker Compose. Não foi pensado pra produção.

Suporta rodar até **3 ambientes (chips) em paralelo** — cada um com seu próprio WhatsApp, banco e aquecimento independentes — acessíveis por uma **landing** de seleção. Veja [Como rodar](#como-rodar-localhost).

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
- ~3 GB livres em disco pras imagens (postgres, redis, waha, .NET runtime, node) — mais por ambiente extra (B/C).
- Portas livres no host:
  - **Ambiente único (A):** `5080`, `5173`, `3000`, `5432`, `6379`.
  - **Multi-ambiente (A+B+C):** acima + `5175` (landing), `5174`/`5176` (web B/C), `5081`/`5082` (API B/C), `3001`/`3002` (WAHA B/C), e Postgres/Redis internos de cada stack.

## Como rodar (localhost)

O sistema é 100% conteinerizado — sobe via Docker Compose. Há os modos de **ambiente único** (abaixo) e o de **múltiplos ambientes** (mais adiante).

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

> ⚠️ **Não rode `docker compose -f docker-compose.dev.yml up` sozinho.** O `docker-compose.dev.yml` é um *override* — só faz sentido empilhado **em cima** do base com os dois `-f` na ordem acima. O mapeamento de porta da Api (`5080:8080`) mora só no base; sem ele a Api sobe sem porta publicada e a UI quebra com `ERR_CONNECTION_REFUSED` em `http://localhost:5080`.
>
> Como conferir se subiu certo: no `docker compose ps` os containers da app devem se chamar `mtrx-api`, `mtrx-web`, `mtrx-dispatcher`. Se aparecerem como `mtrxsys-api-1` (sufixo `-1`), foi sem o base — derrube com `docker compose down` e suba de novo com os dois `-f` (ou use `dev.cmd`).

### Modo multi-ambiente (3 chips em paralelo)

Pra operar **vários WhatsApp ao mesmo tempo** (cada chip num ambiente isolado, com banco, WAHA e aquecimento próprios), use os helpers `*-all.cmd`. Eles empilham `docker-compose.yml` (Stack 1 / Ambiente A) + `docker-compose-2.yml` (Stack 2 / Ambiente B + a **landing**) + `docker-compose-3.yml` (Stack 3 / Ambiente C).

```powershell
up-all.cmd      # sobe os 3 stacks em modo produção e abre a landing
dev-all.cmd     # igual, mas o Stack 1 sobe com HMR (Vite + dotnet watch); B e C ficam estáticos
down-all.cmd    # derruba os 3 stacks (preserva dados)
```

O `up-all.cmd` aguarda as 3 APIs ficarem `healthy` (containers `mtrx-api`, `mtrx2-api`, `mtrx3-api`) e abre a **landing em `http://localhost:5175`**.

**Landing (`localhost:5175`):** mostra um card por ambiente (A/B/C) já preenchido com as credenciais. Ao entrar, ela autentica no backend daquele ambiente e abre o dashboard correspondente numa aba nova, passando o JWT pelo fragment da URL (`#token=...`) — que o app consome e limpa do histórico na hora. Cada card mostra **"🟢 Em uso"** e trava quando aquele dashboard está aberto, via *presence tracking* (veja [Presence tracking](#presence-tracking-landing-multi-ambiente)).

Os ambientes B e C são **espelhos** do A (mesmo código), diferindo só nas portas, no banco/WAHA isolados e no admin semeado — defina `SEED2_*` / `SEED3_*` no ambiente pra trocar as credenciais padrão.

### Comandos úteis

```bash
docker compose ps                    # ver status dos containers
docker compose logs -f api           # ver logs de um serviço
docker compose down                  # parar tudo (preserva dados)
docker compose down -v               # parar e apagar volumes (perde pareamento WhatsApp)
docker compose up -d --build api     # rebuildar apenas um serviço
```

**Multi-ambiente — cada `-f` mira um stack.** Sem `-f`, o comando age **só no Ambiente A** (`docker-compose.yml`). Pra atingir B/C/… aponte pro arquivo do stack:

```bash
docker compose -f docker-compose-2.yml logs -f api            # logs do Ambiente B
docker compose -f docker-compose-2.yml up -d --build api web  # rebuildar só api+web do B
docker compose -f docker-compose-3.yml up -d --build api web  # idem, Ambiente C
```

Editou **código compartilhado** (`src/...`) e quer refletir num ambiente em modo produção? Como não há hot reload nele, precisa rebuildar o serviço afetado (`api` recompila C#, `web` reentra o bundle do front). `up-all.cmd` / `dev-all.cmd` rebuildam **todos** os stacks de uma vez.

### Primeira vez

A primeira execução demora 2-5 minutos (build das 4 imagens custom + pull das oficiais). As próximas usam o cache do Docker e sobem em ~10 segundos.

Migrations do banco rodam automaticamente no boot da Api. Usuário admin (`admin@local` / `admin123!`) é criado no primeiro boot se a tabela `users` estiver vazia.

## URLs

**Ambiente único (A):**

- Web: http://localhost:5173 — UI principal
- Api: http://localhost:5080/swagger — endpoints REST
- WAHA dashboard: http://localhost:3000/dashboard (creds não funcionam em WAHA Core; o MtrxSys faz tudo que o dashboard faria)
- Postgres: 5432
- Redis: 6379

**Multi-ambiente** (quando subido via `up-all.cmd` / `dev-all.cmd`):

| Ambiente | Landing/Web | API (swagger) | WAHA (parear celular) |
|----------|-------------|---------------|------------------------|
| Landing  | http://localhost:5175 | — | — |
| A / Chip A | http://localhost:5173 | http://localhost:5080/swagger | http://localhost:3000 |
| B / Chip B | http://localhost:5174 | http://localhost:5081/swagger | http://localhost:3001 |
| C / Chip C | http://localhost:5176 | http://localhost:5082/swagger | http://localhost:3002 |

## Login padrão

Criados automaticamente no primeiro startup de cada stack se a tabela `users` estiver vazia. JWT dura 7 dias.

| Ambiente | Email | Senha |
|----------|-------|-------|
| A | `admin@local` | `admin123!` |
| B | `admin-b@local` | `chipB123!` |
| C | `admin-c@local` | `chipC123!` |

As credenciais de B e C são configuráveis via `SEED2_ADMIN_EMAIL`/`SEED2_ADMIN_PASS` e `SEED3_ADMIN_EMAIL`/`SEED3_ADMIN_PASS`. A landing já vem com os campos preenchidos.

## Fluxo de uso

A UI tem 4 abas (aparecem só depois que o WhatsApp está conectado): **Chat · Grupos · Contatos · Disparo**.

1. **Login** em `http://localhost:5173` (`admin@local` / `admin123!`).
2. **Onboarding WhatsApp** — se a sessão WAHA não estiver `WORKING`, a UI mostra o QR direto na página (rotaciona a cada 20s). Escanear no celular: Aparelhos conectados → Conectar um aparelho.
3. **Grupos** — lista os grupos que você participa. "Importar contatos" salva cada participante como `Contact`, marcado com `GroupTag = nome do grupo`. O **próprio número conectado é excluído** automaticamente, e participantes ocultos (`@lid`) sem telefone resolvível são pulados.
4. **Contatos** — contatos agrupados por grupo (accordion). Mostra Nome / Telefone / Status e, pra quem deu opt-out, um botão **Reativar**. Cada grupo exporta pra **Excel** (.xlsx).
5. **Disparo** — fluxo em etapas, pensado pra não dar tiro no pé:
   - **Pote de mensagens (rodízio):** você cadastra várias mensagens; cada contato recebe **uma aleatória** do pote (com Spintax dentro de cada uma). Checkbox seleciona quais entram no rodízio.
   - **Público:** "todos" ou "só quem respondeu".
   - **Adicionar para disparar** → mostra **quantos** contatos entram e pede **confirmação** antes de enfileirar.
   - **Iniciar envios** → o motor começa a enviar. Dá pra **Parar** (pausa) e retomar; ao retomar, continua só os que estão "Na fila".
   - **Relatório** em tempo real (pendentes / enviados / falhas / pulados) com export pra **Excel** e botão **Renovar lista** (faz backup do relatório completo em .xlsx e zera os resultados pra recomeçar).
6. **Chat** — serve de "está funcionando?": conversas chegam via webhook (polling de ~10s na UI). Botão "Sincronizar" no topo puxa histórico; além disso há **auto-sync a cada 60s** (não precisa clicar).

### Classificação automática (funil)

O contato anda no funil sozinho conforme o que acontece — os nomes internos (em inglês, esperados pela API) aparecem traduzidos na tela:

| Interno  | Tela        | Quando                                              |
|----------|-------------|-----------------------------------------------------|
| Lead     | Novo        | recém-importado / criado                            |
| Qualified| Respondeu   | o contato respondeu qualquer coisa (≠ "sair")       |
| Proposal | Negociando  | manual                                              |
| Won      | Cliente     | manual                                              |
| Lost     | Descartado  | o contato pediu pra sair ("SAIR" etc.) → opt-out    |

**Opt-out:** se a pessoa responder "sair" (e variações), ela é marcada como opt-out + "Descartado", recebe **uma** mensagem de confirmação e **não entra mais** em disparos. Funciona mesmo quando a resposta chega por `@lid` (número oculto) — o sistema resolve o LID pro telefone real via WAHA. O admin pode desfazer no botão **Reativar** (aba Contatos).

## Funcionalidades por área

### Contatos / funil

- `Contact` com `Phone E.164`, `Name`, `GroupTag`, `Theme`, `OptInAt`, `OptOutAt`, `LastSentAt`, `Stage` (Lead/Qualified/Proposal/Won/Lost), `StageChangedAt`
- Importação por grupo: exclui o próprio número conectado e pula participantes sem telefone resolvível
- Backfill de nome: contato importado sem nome ganha o nome público quando responde
- Classificação automática no inbound (resposta → "Respondeu"; "SAIR" → opt-out + "Descartado")
- `ContactNote` (notas livres), `ContactTag` + `ContactTagAssignment` (tags), `ContactStageChange` (auditoria de stage)
- `Conversation` (chat agrupado por `waChatId`) + `ChatMessage` (idempotente por `WaMessageId`)
- Tratamento de `@c.us` (telefone real), `@g.us` (grupo) e `@lid` (número oculto) — o `@lid` é **resolvido pro telefone real** via WAHA pra casar com o contato no opt-out

### Disparo

- **Rodízio de mensagens:** o disparo recebe um pote de templates (`TemplateIds[]`); cada contato sorteia uma mensagem (via `IRandomSource`)
- **Fluxo em etapas:** preparar (enfileira, pausado) → revisar contagem → iniciar → parar/retomar → limpar fila
- **Pausa manual (kill switch):** `SystemState.IsManuallyPaused`; o `DispatchEngine` checa no topo de cada ciclo e para
- `SpintaxExpander` — `{a|b|{c|d}}` recursivo, escape `\{ \| \}`, depth 8, output 4 KB max
- `MessageComposer` — Spintax + placeholders `{{name|default}}`, `{{phone}}`, `{{group}}`, `{{theme}}`
- `OptOutDetector` — detecta pedidos de saída em respostas curtas, ignorando frases longas
- `DelayPolicy` — random uniforme entre `DelayMin/MaxSeconds` (60-180s)
- `TypingSimulator` — typing proporcional ao texto com jitter
- `WarmupManager` — teto de envios/dia por uma curva configurável (`Warmup:Curve`); curva do Dispatcher hoje: `[10, 15, 25, 40, 60, 80, 100]` (default interno se nenhuma for dada: igual). **A curva avança por dias REALMENTE usados, não por calendário:** o índice é a contagem de dias *anteriores a hoje* com ≥1 envio (`CountActiveDaysBeforeAsync`), então chip parado fica no mesmo nível e a 1ª mensagem do dia já entra com o teto do dia atual. Suporta **bônus manual por dia** (`BonusToday`) e modo **"Disparar todos"** (`UnlimitedToday`, sem teto pra hoje); ao bater o teto efetivo (`AtCap`), a UI abre um modal pra liberar mais
- `CircuitBreaker` — para em N falhas consecutivas, abre por X minutos
- `DispatchEngine` — loop: pausa manual? → breaker check → warmup check → dequeue → compose → typing → send → audit → delay
- `SendAuditEntry` — log de cada envio (telefone, texto renderizado, timings)
- Relatório de envios (`/report`, `/status`) + export pra Excel (.xlsx) e "Renovar lista" (backup + zera)

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
- Inbound resolve telefone via `@c.us` ou `@lid` (traduzido pra E.164 via WAHA), classifica o contato e, se for opt-out novo, envia **uma** confirmação de saída (entregas duplicadas falham no `SaveChanges` antes do envio → confirmação sai só uma vez)
- Auto-sync periódico (`WhatsAppAutoSyncService`, padrão 60s): o webhook só dispara em mensagens; entrar num grupo não gera mensagem, então o loop garante que o grupo apareça sem sync manual

### Presence tracking (landing multi-ambiente)

Serve pra a **landing** travar o card de um ambiente enquanto o dashboard dele está aberto (evita dois operadores no mesmo chip). É baseado em **conexão (SSE)**, não em heartbeat:

- O **dashboard** (`App.tsx`) abre um `EventSource` pra `GET /api/presence/connect` do próprio backend e o mantém aberto enquanto a aba existir. Reconecta sozinho se a conexão cair (ex.: API reiniciou).
- `PresenceTracker` (singleton, por API) conta as **conexões abertas**; `GET /api/presence/status` → `{ active, connections }` com `active = connections > 0`. Endpoints **anônimos** (`PresenceEndpoints`).
- A **landing** consulta `/api/presence/status` de cada ambiente a cada 2s e trava o card quando `active`. Destrava só após **2 leituras inativas seguidas** (debounce, pra um F5/reconexão do dashboard não piscar o card). Se o backend estiver fora do ar, *fail open* (destrava).

> **Por que conexão e não heartbeat** (correção do "card não trava fora da aba"): um heartbeat depende de um timer de JS, e navegadores **estrangulam timers de abas em segundo plano** (Chrome ~1x/min) e ainda **congelam a aba** (*Page Lifecycle / freeze*) após ~5min — o timer para e o card destravava sozinho com o dashboard ainda aberto. Com SSE, **quem segura a conexão viva é o navegador, não o JS**: minimizar, mandar pra segundo plano ou congelar a aba **não** derruba a conexão → continua "em uso", que é a intenção. Quando a aba fecha, navega ou o processo morre (crash/kill), o socket cai e o servidor percebe na hora via `RequestAborted` (mais o keepalive de 10s pra detectar quedas sem fechamento limpo) → **destrava automático, sem ação manual**.
>
> Detalhe relacionado: o `EventSource` em `App.tsx` usa o mesmo fallback do `client.ts` (`VITE_API_URL ?? "http://localhost:5080"`). Sem isso, o Ambiente A em **modo dev** (que não injeta `VITE_API_URL`) não abriria a conexão e o card A nunca travava. Nos ambientes de produção o `VITE_API_URL` vem do build arg (ou do default do Dockerfile).

## Configurações importantes (`appsettings.json`)

```json
"Dispatch": {
  "SessionId": "default",
  "DelayMinSeconds": 60,
  "DelayMaxSeconds": 180,
  "TypingMinSeconds": 2,
  "TypingMaxSeconds": 5,
  "TypingJitter": 0.15
},
"CircuitBreaker": { "FailureThreshold": 3, "OpenDurationMinutes": 120 },
"Warmup": { "Curve": [10, 15, 25, 40, 60, 80, 100], "StartedOnUtc": "2026-05-24" },
"Jwt": { "AccessTokenMinutes": 10080 },
"Seed": { "Admin": { "Email": "admin@local", "Password": "admin123!" } }
```

## Variáveis de ambiente

Veja `.env.example` na raiz. As principais são `WAHA_API_KEY`, `JWT_SIGNING_KEY`, `PG_PASS`. No multi-ambiente, as credenciais do admin semeado de cada stack vêm de `SEED2_ADMIN_EMAIL`/`SEED2_ADMIN_PASS` (Ambiente B) e `SEED3_ADMIN_EMAIL`/`SEED3_ADMIN_PASS` (Ambiente C) — com defaults `admin-b@local`/`chipB123!` e `admin-c@local`/`chipC123!`.

## Notas

- **Localhost only**. Não foi pensado pra produção — sem HTTPS, sem Docker Secrets, sem rate limit.
- **WAHA Core ignora `WAHA_DASHBOARD_*`** — dashboard nativo do WAHA não loga com `admin/admin`. Use o MtrxSys.
- **Privacidade do WhatsApp** — contatos com privacidade de número ativada aparecem como `@lid`; na importação eles são pulados (sem telefone real), mas em respostas o MtrxSys tenta resolver o LID pro telefone via WAHA pra não perder o opt-out.
- **Warmup começa em 10 envios/dia** e só sobe a cada dia de uso real (não por data de calendário). Pra liberar mais cedo, ajuste o array `Warmup:Curve` ou use o **bônus / "Disparar todos"** na UI. Atenção: a curva fica no `appsettings.json` de **cada serviço**; quem manda no envio é o **Dispatcher** (`src/MtrxSys.Dispatcher/appsettings.json`). `Warmup:StartedOnUtc` é só pra exibir "iniciado em ..." — não determina mais o índice da curva.

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
- `OptOutDetector` (comandos de saída vs frases longas)
- `WebhookIngestionService` (idempotência, filtros, grupos vs 1:1, opt-out por palavra-chave, opt-out via `@lid` resolvido)
- `Result<T>` (smoke tests)
