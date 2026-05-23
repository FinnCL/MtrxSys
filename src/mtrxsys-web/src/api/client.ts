import type {
  ChatMessage,
  Contact,
  ContactDetail,
  ContactNote,
  Conversation,
  ConversationWithMessages,
  DispatchFilter,
  DispatchJob,
  DispatchStats,
  Group,
  ImportResult,
  LoginResponse,
  MessageSlot,
  MessageTemplate,
  Stage,
  Tag,
  WahaStatus,
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
  const resp = await fetch(`${baseUrl}${path}`, { ...rest, headers: finalHeaders });
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
  listConversations: (limit = 50, offset = 0) =>
    request<Conversation[]>(`/api/conversations?limit=${limit}&offset=${offset}`),
  getConversationMessages: (id: string, limit = 50, offset = 0) =>
    request<ConversationWithMessages>(`/api/conversations/${id}/messages?limit=${limit}&offset=${offset}`),
  sendMessage: (conversationId: string, text: string) =>
    request<ChatMessage>(`/api/conversations/${conversationId}/messages`, {
      method: "POST",
      body: JSON.stringify({ text }),
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
  createTemplate: (contentSpintax: string, slot: MessageSlot = "Greeting") =>
    request<MessageTemplate>("/api/templates", {
      method: "POST",
      body: JSON.stringify({ contentSpintax, slot }),
    }),
  dispatch: (templateId: string, filter: DispatchFilter) =>
    request<{ scheduled: number }>("/api/dispatch", {
      method: "POST",
      body: JSON.stringify({ templateId, filter }),
    }),
  dispatchStats: () => request<DispatchStats>("/api/dispatch/stats"),
  dispatchJobs: (limit = 50) => request<DispatchJob[]>(`/api/dispatch/jobs?limit=${limit}`),
};
