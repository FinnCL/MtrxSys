# MtrxSys

Ferramenta de WhatsApp pra **salvar contatos de grupos** e **disparar mensagens em massa** com proteções anti-ban (Spintax, rodízio de mensagens, delay, typing simulado, warm-up, circuit breaker). Inclui **opt-out automático** ("SAIR"), classificação automática de quem respondeu e relatório de envios com exportação pra Excel.

> Roda **só em localhost** via Docker Compose. Não foi pensado pra produção.

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

> ⚠️ **Não rode `docker compose -f docker-compose.dev.yml up` sozinho.** O `docker-compose.dev.yml` é um *override* — só faz sentido empilhado **em cima** do base com os dois `-f` na ordem acima. O mapeamento de porta da Api (`5080:8080`) mora só no base; sem ele a Api sobe sem porta publicada e a UI quebra com `ERR_CONNECTION_REFUSED` em `http://localhost:5080`.
>
> Como conferir se subiu certo: no `docker compose ps` os containers da app devem se chamar `mtrx-api`, `mtrx-web`, `mtrx-dispatcher`. Se aparecerem como `mtrxsys-api-1` (sufixo `-1`), foi sem o base — derrube com `docker compose down` e suba de novo com os dois `-f` (ou use `dev.cmd`).

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
- `WarmupManager` — curva de envios/dia configurável (`Warmup:Curve`); padrão do Dispatcher: `[20, 40, 80, 150, 250, 400, 500]`
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
"Warmup": { "Curve": [20, 40, 80, 150, 250, 400, 500] },
"Jwt": { "AccessTokenMinutes": 10080 },
"Seed": { "Admin": { "Email": "admin@local", "Password": "admin123!" } }
```

## Variáveis de ambiente

Veja `.env.example` na raiz. As principais são `WAHA_API_KEY`, `JWT_SIGNING_KEY`, `PG_PASS`.

## Notas

- **Localhost only**. Não foi pensado pra produção — sem HTTPS, sem Docker Secrets, sem rate limit.
- **WAHA Core ignora `WAHA_DASHBOARD_*`** — dashboard nativo do WAHA não loga com `admin/admin`. Use o MtrxSys.
- **Privacidade do WhatsApp** — contatos com privacidade de número ativada aparecem como `@lid`; na importação eles são pulados (sem telefone real), mas em respostas o MtrxSys tenta resolver o LID pro telefone via WAHA pra não perder o opt-out.
- **Warmup começa em 20 envios/dia**. Pra liberar mais cedo, ajuste o array `Warmup:Curve`. Atenção: a curva fica no `appsettings.json` de **cada serviço**; quem manda no envio é o **Dispatcher** (`src/MtrxSys.Dispatcher/appsettings.json`).

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
