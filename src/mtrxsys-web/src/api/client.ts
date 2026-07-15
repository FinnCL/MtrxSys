import type {
  ChatMessage,
  CircleMember,
  Contact,
  ContactDetail,
  ContactGroupTag,
  ContactNote,
  Conversation,
  ConversationCounts,
  ConversationStatus,
  ConversationWithMessages,
  DispatchFilter,
  DispatchReportItem,
  DispatchStats,
  Group,
  GroupLinkPage,
  GroupLinkStatus,
  GroupMember,
  HumanPhaseStatus,
  ImportResult,
  ManualImportResult,
  LoginResponse,
  MessageSlot,
  MessageTemplate,
  PhoneContact,
  Stage,
  Tag,
  WahaStatus,
  WarmupStatus,
} from "./types";

const baseUrl = (import.meta.env.VITE_API_URL as string | undefined) ?? "http://localhost:5080";

const TOKEN_KEY = "mtrx_token";

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY);
}

export function setToken(token: string | null): void {
  if (token) {
    localStorage.setItem(TOKEN_KEY, token);
  } else {
    localStorage.removeItem(TOKEN_KEY);
  }
}

export class ApiError extends Error {
  status: number;
  constructor(status: number, message: string) {
    super(message);
    this.status = status;
  }
}

async function request<T>(path: string, init: RequestInit & { auth?: boolean } = {}): Promise<T> {
  const { auth = true, headers, ...rest } = init;
  const finalHeaders = new Headers(headers);
  if (!finalHeaders.has("content-type") && rest.body) {
    finalHeaders.set("content-type", "application/json");
  }
  if (auth) {
    const token = getToken();
    if (token) {
      finalHeaders.set("authorization", `Bearer ${token}`);
    }
  }
  const send = () => fetch(`${baseUrl}${path}`, { ...rest, headers: finalHeaders });
  let resp = await send();
  // Um 401 em request autenticada pode ser TRANSITÓRIO (API reiniciando, blip de rede) — antes de
  // tratar como sessão expirada, tenta UMA vez de novo após um respiro; só desloga se persistir.
  // Isso resolve o caso real: o polling (GET de 4-5s) capturava um 401 momentâneo e jogava o
  // usuário pra fora no meio do disparo.
  // SÓ re-tenta métodos IDEMPOTENTES (GET/HEAD). Um POST (ex.: /api/dispatch) NUNCA é re-tentado
  // automaticamente: se a 1ª chamada chegou a rodar o handler e ainda assim retornou 401 (reinício/
  // proxy), re-executar duplicaria a ação (enfileiraria o disparo 2x). Logout em POST é seguro.
  const method = (rest.method ?? "GET").toUpperCase();
  const isIdempotent = method === "GET" || method === "HEAD";
  if (resp.status === 401 && auth && isIdempotent && getToken()) {
    await new Promise((r) => setTimeout(r, 800));
    resp = await send();
  }
  if (!resp.ok) {
    if (resp.status === 401 && auth) {
      handleAuthExpired();
    }
    let message = resp.statusText;
    try {
      const body = await resp.json();
      message = body.detail ?? body.title ?? body.message ?? message;
    } catch {
      // ignore
    }
    throw new ApiError(resp.status, message);
  }
  if (resp.status === 204) {
    return undefined as T;
  }
  return resp.json() as Promise<T>;
}

function handleAuthExpired(): void {
  if (!getToken()) return;
  setToken(null);
  localStorage.removeItem("mtrx_user");
  if (typeof window !== "undefined") {
    window.location.reload();
  }
}

// Estado do Android em container (opção de servidor da aba "Celular"), espelha PhoneStatus do backend.
// state: unavailable (sem docker/host) | not_created (container não existe) | created | exited | running.
export interface PhoneStatus {
  state: string;
  running: boolean;
  viewUrl: string | null;
}

// Modo da aba "Celular" (toggle único), PERSISTIDO no banco (system_state). Fonte da verdade do que a
// página renderiza — não é derivado do container do emulador estar ligado.
//  "WahaOnly" = WAHA + aparelho real físico (sem emulador).
//  "Emulator" = Emulador (Android em container) + WAHA.
export type PhoneMode = "WahaOnly" | "Emulator";

// Identidade real do aparelho virtual (WAHA): número + nome do chip pareado e status da sessão.
// status === "Working" = conectado. Vem do /api/presence/chip (mesmo que a landing usa).
export interface ChipIdentity {
  status: string;
  phone: string | null;
  name: string | null;
  breakerOpen?: boolean;
  // Proxy REALMENTE aplicado na sessão WAHA (host:porta) ou null se o chip sai pelo IP da máquina.
  proxy?: string | null;
  // IP:porta do proxy REAL que a ponte gost usa como upstream (ex.: 200.160.34.167:12323) — o IP
  // pelo qual o chip de fato SAI. Mostrado abaixo do badge gost como "↳ sai por ...".
  proxyReal?: string | null;
}

export const api = {
  login: (email: string, password: string) =>
    request<LoginResponse>("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password }),
      auth: false,
    }),
  wahaStatus: () => request<{ status: WahaStatus; session: string }>("/api/waha/status"),
  wahaStart: () => request<{ status: WahaStatus }>("/api/waha/start", { method: "POST" }),
  // Reset completo: desconecta E apaga a sessão (sem resíduo no volume) e recria → QR novo.
  // Usado ao trocar de número, pra o pareamento ser dinâmico (não restaura o aparelho antigo).
  // O endpoint /api/waha/logout continua existindo no backend, mas o front sempre usa o reset.
  wahaReset: () => request<{ status: WahaStatus }>("/api/waha/reset", { method: "POST" }),
  // Religa conversas órfãs (sem contato) ao contato — auto-cura ao reconectar.
  relinkConversations: () =>
    request<{ linked: number }>("/api/conversations/relink", { method: "POST" }),
  wahaSync: (messagesPerChat?: number) =>
    request<{
      chatsTouched: number;
      messagesImported: number;
      contactsCreated: number;
      failures: string[];
    }>(`/api/waha/sync${messagesPerChat ? `?messagesPerChat=${messagesPerChat}` : ""}`, {
      method: "POST",
    }),
  // Identidade real do aparelho virtual (WAHA) — número/nome do chip pareado. Mesma fonte da landing.
  phoneIdentity: () => request<ChipIdentity>("/api/presence/chip", { auth: false }),
  // Modo persistido da aba "Celular" (WahaOnly / Emulator) — o toggle único lê e grava aqui.
  phoneMode: () => request<{ mode: PhoneMode }>("/api/phone/mode"),
  phoneSetMode: (mode: PhoneMode) =>
    request<{ mode: PhoneMode }>("/api/phone/mode", {
      method: "POST",
      body: JSON.stringify({ mode }),
    }),
  // Android em container (opção de servidor) — orquestração pela aba "Celular".
  phoneStatus: () => request<PhoneStatus>("/api/phone/status"),
  phoneProvision: () => request<PhoneStatus>("/api/phone/provision", { method: "POST" }),
  phoneStart: () => request<PhoneStatus>("/api/phone/start", { method: "POST" }),
  phoneStop: () => request<void>("/api/phone/stop", { method: "POST" }),
  phoneBooted: () => request<{ booted: boolean }>("/api/phone/booted"),
  phoneKeepAlive: () => request<void>("/api/phone/keepalive", { method: "POST" }),
  phoneLogs: (tail = 200) => request<{ logs: string }>(`/api/phone/logs?tail=${tail}`),
  // Aplica proxy no emulador (restaurado: ffacd78 removeu o botão; o backend /api/phone/proxy existe).
  phoneSetProxy: (server: string) =>
    request<{ output: string }>("/api/phone/proxy", {
      method: "POST",
      body: JSON.stringify({ server }),
    }),
  phoneInstallWhatsApp: () =>
    request<{ output: string }>("/api/phone/whatsapp/install", { method: "POST" }),
  // Lê o número que o WhatsApp do emulador está registrando (pré-preenche o campo do código).
  phoneWhatsAppNumber: () => request<{ number: string | null }>("/api/phone/whatsapp-number"),
  // Digita texto no emulador (o código do WhatsApp) — `t` vai na query (param string simples do minimal API).
  phoneText: (t: string) =>
    request<{ typed?: boolean }>(`/api/phone/text?t=${encodeURIComponent(t)}`, { method: "POST" }),
  // Botões de navegação do Android (voltar/home/recentes) — envia keyevent via adb no emulador.
  // (Reconstruído: era server-only e um sync o removeu; o backend /api/phone/key + SendKeyAsync existe.)
  phoneKey: (k: "back" | "home" | "recents") =>
    request<{ output: string }>(`/api/phone/key?k=${encodeURIComponent(k)}`, { method: "POST" }),
  wahaQrBlobUrl: async (): Promise<string> => {
    const token = getToken();
    const resp = await fetch(`${baseUrl}/api/waha/qr.png`, {
      headers: token ? { authorization: `Bearer ${token}` } : {},
    });
    if (!resp.ok) {
      throw new ApiError(resp.status, resp.statusText);
    }
    const blob = await resp.blob();
    return URL.createObjectURL(blob);
  },
  // Conexão por código (alternativa ao QR): manda o número (com DDI) e recebe o código de
  // pareamento pra digitar no WhatsApp em "Conectar com número de telefone".
  wahaPairingCode: (phoneNumber: string) =>
    request<{ code: string }>("/api/waha/pairing-code", {
      method: "POST",
      body: JSON.stringify({ phoneNumber }),
    }),
  listConversations: (opts: { status?: ConversationStatus; search?: string; limit?: number; offset?: number } = {}) => {
    const q = new URLSearchParams();
    if (opts.status) q.set("status", opts.status);
    if (opts.search) q.set("search", opts.search);
    q.set("limit", String(opts.limit ?? 50));
    q.set("offset", String(opts.offset ?? 0));
    return request<Conversation[]>(`/api/conversations?${q.toString()}`);
  },
  getConversationCounts: (search?: string) =>
    request<ConversationCounts>(`/api/conversations/counts${search ? `?search=${encodeURIComponent(search)}` : ""}`),
  getConversationMessages: (id: string, limit = 50, offset = 0) =>
    request<ConversationWithMessages>(`/api/conversations/${id}/messages?limit=${limit}&offset=${offset}`),
  sendMessage: (conversationId: string, text: string) =>
    request<ChatMessage>(`/api/conversations/${conversationId}/messages`, {
      method: "POST",
      body: JSON.stringify({ text }),
    }),
  listContacts: (params: { stage?: Stage; groupTag?: string } = {}) => {
    const q = new URLSearchParams();
    if (params.stage) q.set("stage", params.stage);
    if (params.groupTag) q.set("groupTag", params.groupTag);
    const qs = q.toString();
    return request<Contact[]>(`/api/contacts${qs ? `?${qs}` : ""}`);
  },
  listContactGroupTags: () => request<ContactGroupTag[]>("/api/contacts/group-tags"),
  reactivateContact: (id: string) =>
    request<Contact>(`/api/contacts/${id}/reactivate`, { method: "POST" }),
  // Libera o contato pra novo disparo (zera o "já enviado" só dele) — volta ao público do disparo.
  resendContact: (id: string) =>
    request<Contact>(`/api/contacts/${id}/resend`, { method: "POST" }),
  // Descarta (soft delete) um contato: some das listas, do disparo, do chat e do resultado dos envios.
  discardContact: (id: string) =>
    request<{ discarded: boolean }>(`/api/contacts/${id}/discard`, { method: "POST" }),
  // Descarta (soft delete) os contatos de um grupo: somem de tudo, mas ficam no banco (reversível).
  deleteGroupContacts: (groupTag: string) =>
    request<{ deleted: number }>("/api/contacts/delete-by-group", {
      method: "POST",
      body: JSON.stringify({ groupTag }),
    }),
  getContact: (id: string) => request<ContactDetail>(`/api/contacts/${id}`),
  patchContact: (id: string, payload: { stage?: Stage; addTags?: string[]; removeTags?: string[] }) =>
    request<Contact>(`/api/contacts/${id}`, {
      method: "PATCH",
      body: JSON.stringify(payload),
    }),
  addNote: (contactId: string, body: string) =>
    request<ContactNote>(`/api/contacts/${contactId}/notes`, {
      method: "POST",
      body: JSON.stringify({ body }),
    }),
  listTags: () => request<Tag[]>("/api/tags"),
  createTag: (name: string, color: string | null) =>
    request<Tag>("/api/tags", {
      method: "POST",
      body: JSON.stringify({ name, color }),
    }),
  listGroups: () => request<Group[]>("/api/groups"),
  // Cria o grupo PELO sistema — é o que faz "esse grupo é meu" ser fato e não palpite (o WAHA não
  // diz quem criou). O grupo criado volta com isMine=true.
  createGroup: (body: { name: string; phones: string[] }) =>
    request<Group>("/api/groups", { method: "POST", body: JSON.stringify(body) }),
  // Telefones de quem está dentro do grupo.
  listGroupMembers: (groupId: string) =>
    request<GroupMember[]>(`/api/groups/${encodeURIComponent(groupId)}/participants`),
  // Declara que um grupo EXISTENTE é seu — o caminho normal, já que o grupo de aquecimento nasce no
  // aparelho físico (criar por API num chip frio é sinal de bot). Idempotente.
  claimGroup: (groupId: string) =>
    request<{ claimed: boolean; alreadyClaimed: boolean; exempt: boolean }>(
      `/api/groups/${encodeURIComponent(groupId)}/claim`,
      { method: "POST" },
    ),
  // Desfaz a declaração. O grupo continua no WhatsApp; a isenção cai junto.
  unclaimGroup: (groupId: string) =>
    request<{ claimed: boolean; wasClaimed: boolean }>(
      `/api/groups/${encodeURIComponent(groupId)}/claim`,
      { method: "DELETE" },
    ),
  // Liga/desliga a dispensa da trava de "já enviei pra esse" pros membros deste grupo. Ao LIGAR, o
  // backend re-lê os membros no WhatsApp e fotografa — se o WhatsApp estiver fora, falha e NÃO liga.
  setGroupExemption: (groupId: string, enabled: boolean) =>
    request<{ enabled: boolean; members: number }>(
      `/api/groups/${encodeURIComponent(groupId)}/exemption`,
      { method: "PATCH", body: JSON.stringify({ enabled }) },
    ),
  importGroup: (groupId: string, groupTag?: string) =>
    request<ImportResult>(`/api/groups/${encodeURIComponent(groupId)}/import`, {
      method: "POST",
      body: JSON.stringify({ groupTag: groupTag ?? null }),
    }),
  // Sai do grupo (número conectado deixa o grupo). Tolerante a grupo-fantasma no backend.
  leaveGroup: (groupId: string) =>
    request<{ left: boolean }>(`/api/groups/${encodeURIComponent(groupId)}/leave`, {
      method: "POST",
    }),
  // Coletor de grupos: dispara uma coleta (background), lista os links e entra num grupo.
  collectorCollect: (keyword?: string) =>
    request<{ queued: boolean; keyword: string | null; searchConfigured: boolean }>(
      "/api/collector/collect",
      {
        method: "POST",
        body: JSON.stringify({ keyword: keyword ?? null }),
      },
    ),
  // Info do motor de busca pro card (sem URL): qual motor, requisições feitas e o último erro
  // (ex.: "Serper recusou: limite/sem crédito") pra mostrar POR QUE a busca parou.
  collectorSearchInfo: () =>
    request<{ engine: string; configured: boolean; requestCount: number; lastError: string | null }>(
      "/api/collector/search-info",
    ),
  // Estado da trava anti-ban de entrada (deixa o limite explícito no painel).
  collectorJoinStatus: () =>
    request<{ joinsToday: number; maxPerDay: number; remaining: number; waitSeconds: number }>(
      "/api/collector/join-status",
    ),
  collectorLinks: (
    params: { keyword?: string; status?: GroupLinkStatus; limit?: number; offset?: number } = {},
  ) => {
    const q = new URLSearchParams();
    if (params.keyword) q.set("keyword", params.keyword);
    if (params.status) q.set("status", params.status);
    if (params.limit !== undefined) q.set("limit", String(params.limit));
    if (params.offset !== undefined) q.set("offset", String(params.offset));
    const qs = q.toString();
    return request<GroupLinkPage>(`/api/collector/links${qs ? `?${qs}` : ""}`);
  },
  collectorJoin: (code: string) =>
    request<{ joined: boolean; groupId: string; name: string; canImport: boolean }>(
      `/api/collector/links/${encodeURIComponent(code)}/join`,
      { method: "POST" },
    ),
  // Entrada manual: cola links (um por linha), valida na hora (só vivos aparecem como "Pronto").
  collectorAddManual: (links: string[], keyword?: string) =>
    request<{
      added: number;
      live: number;
      dead: number;
      duplicates: number;
      pendingValidation: number;
    }>("/api/collector/links/manual", {
      method: "POST",
      body: JSON.stringify({ links, keyword: keyword ?? null }),
    }),
  addManualContacts: (numbers: string[], groupTag?: string) =>
    request<ManualImportResult>("/api/contacts/manual", {
      method: "POST",
      body: JSON.stringify({ numbers, groupTag: groupTag ?? null }),
    }),
  listTemplates: () => request<MessageTemplate[]>("/api/templates"),
  createTemplate: (
    contentSpintax: string,
    slot: MessageSlot = "Greeting",
    image?: { base64: string; mimeType: string },
  ) =>
    request<MessageTemplate>("/api/templates", {
      method: "POST",
      body: JSON.stringify({
        contentSpintax,
        slot,
        imageBase64: image?.base64,
        imageMimeType: image?.mimeType,
      }),
    }),
  // Miniatura do template: busca os bytes com auth (o endpoint exige Bearer, então
  // <img src> direto daria 401) e devolve um object URL. Espelha wahaQrBlobUrl.
  templateImageBlobUrl: async (id: string): Promise<string> => {
    const token = getToken();
    const resp = await fetch(`${baseUrl}/api/templates/${id}/image`, {
      headers: token ? { authorization: `Bearer ${token}` } : {},
    });
    if (!resp.ok) {
      throw new ApiError(resp.status, resp.statusText);
    }
    const blob = await resp.blob();
    return URL.createObjectURL(blob);
  },
  deleteTemplate: (id: string) =>
    request<void>(`/api/templates/${id}`, { method: "DELETE" }),
  dispatch: (templateIds: string[], filter: DispatchFilter) =>
    request<{ scheduled: number; templatesUsed: number }>("/api/dispatch", {
      method: "POST",
      body: JSON.stringify({ templateIds, filter }),
    }),
  dispatchStats: () => request<DispatchStats>("/api/dispatch/stats"),
  dispatchReport: (status?: string, limit = 1000) => {
    const q = new URLSearchParams();
    if (status) q.set("status", status);
    q.set("limit", String(limit));
    return request<DispatchReportItem[]>(`/api/dispatch/report?${q.toString()}`);
  },
  dispatchStatus: () =>
    request<{ paused: boolean; circuitOpen: boolean; circuitOpenUntil: string | null }>(
      "/api/dispatch/status",
    ),
  // Saúde de ENTREGA (sensor anti-shadow-restriction): dos envios das últimas 24h, quantos entregaram
  // (ack >= 2). rate = null quando não houve envio na janela.
  deliveryHealth: () =>
    request<{ windowHours: number; sent: number; delivered: number; rate: number | null }>(
      "/api/dispatch/delivery-health",
    ),
  warmupStatus: () => request<WarmupStatus>("/api/dispatch/warmup"),
  restartWarmup: () =>
    request<{ startedOn: string }>("/api/dispatch/warmup/restart", { method: "POST" }),
  // Reconcilia o aquecimento com o número conectado; reinicia sozinho se o chip mudou.
  reconcileWarmup: () =>
    request<{ changed: boolean; phone: string | null }>("/api/dispatch/warmup/reconcile", {
      method: "POST",
    }),
  // Libera envios acima do teto só pra hoje: { all: true } solta tudo, ou { extra: N }.
  releaseWarmup: (body: { extra?: number; all?: boolean }) =>
    request<{ bonusToday: number; unlimited: boolean }>("/api/dispatch/warmup/release", {
      method: "POST",
      body: JSON.stringify({ extra: body.extra ?? null, all: body.all ?? false }),
    }),
  // (o antigo dispatchJobs foi removido — substituído por dispatchReport)
  pauseDispatch: () => request<{ paused: boolean }>("/api/dispatch/pause", { method: "POST" }),
  resumeDispatch: () => request<{ paused: boolean }>("/api/dispatch/resume", { method: "POST" }),
  clearQueue: () => request<{ cleared: number }>("/api/dispatch/clear", { method: "POST" }),
  resetResults: () => request<{ cleared: number }>("/api/dispatch/reset", { method: "POST" }),
  audienceCount: (params: { engagedOnly?: boolean; groupTag?: string } = {}) => {
    const q = new URLSearchParams();
    if (params.engagedOnly) q.set("engagedOnly", "true");
    if (params.groupTag) q.set("groupTag", params.groupTag);
    const qs = q.toString();
    return request<{ count: number }>(`/api/dispatch/audience-count${qs ? `?${qs}` : ""}`);
  },

  // Motor de AQUECIMENTO DE CONVERSA (pool) — distinto do warmupStatus acima (que é a rampa de teto
  // diário do disparo). Aqui é o pool conversando de mão dupla pra ganhar reputação.
  warmupEngineStatus: () =>
    request<{
      featureEnabled: boolean;
      running: boolean;
      memberCount: number;
      groupCount: number;
      startedOn: string | null;
      members: { name: string; phone: string; sentToday: number }[];
    }>("/api/warmup/status"),
  startWarmupEngine: () => request<{ running: boolean }>("/api/warmup/start", { method: "POST" }),
  stopWarmupEngine: () => request<{ running: boolean }>("/api/warmup/stop", { method: "POST" }),

  // FASE HUMANA (dias 1-3 do chip novo): o disparo fica travado enquanto o operador conversa à mão
  // pela aba Chat. applies=false quando não vale pra este chip (recurso desligado ou chip anterior
  // ao corte) — aí a UI não mostra nada.
  humanPhase: () => request<HumanPhaseStatus>("/api/warmup/human-phase"),
  // Agenda do aparelho. Por padrão só os salvos (isMyContact); all=true é escape de diagnóstico
  // caso o engine não preencha a marca.
  phoneContacts: (all = false) =>
    request<PhoneContact[]>(`/api/warmup/phone-contacts${all ? "?all=true" : ""}`),
  listWarmupCircle: () => request<CircleMember[]>("/api/warmup/circle"),
  addToWarmupCircle: (body: { phone: string; name?: string | null }) =>
    request<CircleMember>("/api/warmup/circle", { method: "POST", body: JSON.stringify(body) }),
  removeFromWarmupCircle: (id: string) =>
    request<void>(`/api/warmup/circle/${id}`, { method: "DELETE" }),
  // Liga/desliga o robô que conversa com o círculo durante a fase.
  setHumanPhaseAutoSend: (enabled: boolean) =>
    request<{ autoSendEnabled: boolean }>(`/api/warmup/human-phase/auto/${enabled}`, { method: "POST" }),
};
