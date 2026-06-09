import type {
  ChatMessage,
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
  ImportResult,
  LoginResponse,
  MessageSlot,
  MessageTemplate,
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

export const api = {
  login: (email: string, password: string) =>
    request<LoginResponse>("/api/auth/login", {
      method: "POST",
      body: JSON.stringify({ email, password }),
      auth: false,
    }),
  wahaStatus: () => request<{ status: WahaStatus; session: string }>("/api/waha/status"),
  wahaStart: () => request<{ status: WahaStatus }>("/api/waha/start", { method: "POST" }),
  wahaLogout: () => request<{ status: WahaStatus }>("/api/waha/logout", { method: "POST" }),
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
  // Exclui os contatos de um grupo (e suas conversas/disparos).
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
  importGroup: (groupId: string, groupTag?: string) =>
    request<ImportResult>(`/api/groups/${encodeURIComponent(groupId)}/import`, {
      method: "POST",
      body: JSON.stringify({ groupTag: groupTag ?? null }),
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
};
