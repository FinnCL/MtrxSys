# mtrxsys-web

Frontend do **MtrxSys** — React 19 + TypeScript + Vite 8. É a UI do dashboard de um ambiente (chip): onboarding do WhatsApp (QR), grupos, contatos e disparo em massa.

> Documentação geral do projeto (arquitetura, como subir os ambientes, fluxo de uso, anti-ban) está no [README da raiz](../../README.md). Este aqui cobre só o frontend.

## Rodar

Normalmente o front sobe **conteinerizado** junto do resto via Docker Compose (veja o README da raiz — `start.cmd` pra prod, `dev.cmd` pra HMR). Pra rodar o Vite direto no host (apontando pra uma API já no ar em `localhost:5080`):

```bash
npm install
npm run dev        # Vite dev server (HMR) em http://localhost:5173
```

## Scripts

| Script            | O que faz                                                                 |
|-------------------|---------------------------------------------------------------------------|
| `npm run dev`     | Vite dev server com HMR                                                   |
| `npm run build`   | `tsc -b` (type-check) + `vite build` → bundle estático em `dist/`         |
| `npm run preview` | Serve o bundle de produção localmente                                     |
| `npm run lint`    | ESLint                                                                     |
| `npm run openapi` | Regera `src/api/schema.d.ts` a partir do swagger da API (`localhost:5080`) |

## Configuração

- **`VITE_API_URL`** — base da API. Default (sem a var): `http://localhost:5080`. No multi-ambiente, cada stack injeta a sua via build arg do Docker. Usado tanto pelo `client.ts` quanto pelo `EventSource` de presença em `App.tsx`.

## Estrutura

```
src/
  main.tsx              Entry; monta o App
  App.tsx               Shell: auth gate, abas, EventSource de presença (/api/presence/connect)
  App.css / index.css   Estilos (tokens compartilhados com a landing)
  api/
    client.ts           openapi-fetch tipado contra schema.d.ts
    schema.d.ts          Tipos gerados do swagger (npm run openapi)
  auth/
    AuthContext.tsx     Consome o JWT do fragment (#token=...) vindo da landing e limpa do histórico
  components/
    LoginScreen.tsx       Login (email/senha)
    WhatsAppOnboarding.tsx QR de pareamento (rotaciona a cada ~20s)
    GroupsScreen.tsx      Lista grupos + "Importar contatos"
    ContactsScreen.tsx    Contatos por grupo (accordion), reativar opt-out, export Excel
    ContactPanel.tsx      Detalhe/edição de um contato
    CampaignsScreen.tsx   Disparo: pote de mensagens, público, fila, relatório, modal de teto
    MessageComposer.tsx   Edição das mensagens (Spintax/placeholders)
    ChatThread.tsx        Conversa (mensagens via webhook/polling)
    ConversationList.tsx  Lista de conversas
    ConfirmDialog.tsx     Modal de confirmação
    StatusBadge.tsx       Badge de status da sessão WAHA
  utils/                Helpers
```

## Notas

- **Tipos da API são gerados**, não escritos à mão: rode `npm run openapi` com a API no ar pra atualizar `schema.d.ts` depois de mudar endpoints no backend.
- O **lock de "card em uso"** da landing depende do `EventSource` aberto pelo `App.tsx` (`GET /api/presence/connect`). Detalhes no README da raiz, seção *Presence tracking*.
- As 4 abas (Chat · Grupos · Contatos · Disparo) só aparecem **depois** que o WhatsApp está conectado.
