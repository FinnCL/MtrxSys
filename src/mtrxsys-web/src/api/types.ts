export type WahaStatus = "Unknown" | "Stopped" | "Starting" | "ScanQrCode" | "Working" | "Failed";

export type Stage = "Lead" | "Qualified" | "Proposal" | "Won" | "Lost";

export const ALL_STAGES: Stage[] = ["Lead", "Qualified", "Proposal", "Won", "Lost"];

// Rótulos em PT-BR para exibição. Os valores acima continuam em inglês porque é
// o que a API espera; só o texto que aparece na tela é traduzido.
export const STAGE_LABELS: Record<Stage, string> = {
  Lead: "Novo",
  Qualified: "Respondeu",
  Proposal: "Negociando",
  Won: "Cliente",
  Lost: "Descartado",
};

/** Rótulo + classe do selo de status de um contato. */
export interface ContactStatusBadge {
  label: string;
  className: string;
}

/**
 * Fonte ÚNICA do status exibido de um contato — usada na tabela de Contatos,
 * no painel e na exportação pra que não divirjam. Deriva de (optOutAt, stage,
 * lastSentAt): quem saiu vira "Saiu"; um "Novo" que já recebeu disparo vira
 * "Não respondeu" (mesma ideia do awaitingReply do Chat).
 */
export function contactStatusBadge(
  c: Pick<Contact, "stage" | "optOutAt" | "lastSentAt">,
): ContactStatusBadge {
  if (c.optOutAt) return { label: "Saiu", className: "stage-lost" };
  if (c.stage === "Lead" && c.lastSentAt) {
    return { label: "Não respondeu", className: "stage-no-reply" };
  }
  return { label: STAGE_LABELS[c.stage], className: `stage-${c.stage.toLowerCase()}` };
}

export type Direction = "Inbound" | "Outbound";

// Status (filtro das abas do Chat). A classificação é automática no backend (respondeu →
// "responded"; opt-out → "optedOut"; ainda sem resposta → "awaitingReply").
export type ConversationStatus = "awaitingReply" | "responded" | "optedOut";

export interface Conversation {
  id: string;
  waChatId: string;
  contactId: string | null;
  title: string | null;
  isGroup: boolean;
  lastMessageAt: string | null;
  lastMessagePreview: string | null;
  createdAt: string;
}

// Contagem por status (vinda de /api/conversations/counts) para os números das abas.
export interface ConversationCounts {
  awaitingReply: number;
  responded: number;
  optedOut: number;
  all: number;
}

export interface ChatMessage {
  id: string;
  conversationId: string;
  waMessageId: string;
  direction: Direction;
  authorPhone: string | null;
  body: string;
  timestamp: string;
  mediaUrl: string | null;
}

export interface Contact {
  id: string;
  phoneE164: string;
  name: string | null;
  groupTag: string | null;
  theme: string | null;
  stage: Stage;
  stageChangedAt: string | null;
  optInAt: string | null;
  optOutAt: string | null;
  lastSentAt: string | null;
}

export interface ContactNote {
  id: string;
  contactId: string;
  body: string;
  createdAt: string;
  createdByUserId: string;
}

export interface StageChange {
  id: string;
  fromStage: Stage | null;
  toStage: Stage;
  changedAt: string;
  changedByUserId: string;
}

export interface Tag {
  name: string;
  color: string | null;
  createdAt: string;
}

export interface ContactDetail {
  contact: Contact;
  notes: ContactNote[];
  tags: string[];
  stageHistory: StageChange[];
}

export interface ConversationWithMessages {
  conversation: Conversation;
  messages: ChatMessage[];
}

export interface LoginResponse {
  accessToken: string;
  expiresAt: string;
  userId: string;
  email: string;
  displayName: string;
}

export interface Group {
  id: string;
  name: string;
  participantsCount: number | null;
}

export interface ContactGroupTag {
  groupTag: string;
  count: number;
}

export interface ImportFailure {
  participantId: string;
  phone: string;
  reason: string;
}

export interface ImportResult {
  total: number;
  imported: number;
  duplicated: number;
  failed: number;
  failures: ImportFailure[];
}

export type MessageSlot = "Greeting" | "Intro" | "Hook" | "OptOut";

export interface MessageTemplate {
  id: string;
  slot: MessageSlot;
  contentSpintax: string;
  active: boolean;
  hasImage: boolean;
}

export interface DispatchFilter {
  stage?: Stage;
  tagName?: string;
  groupTag?: string;
  engagedOnly?: boolean;
}

export interface DispatchStats {
  pending: number;
  sent: number;
  failed: number;
  skipped: number;
}

// Estado do aquecimento do chip (vindo de /api/dispatch/warmup). day é base-1.
export interface WarmupStatus {
  phone: string | null;     // número que o aquecimento acompanha
  startedOn: string;
  day: number;
  totalDays: number;
  todayLimit: number;       // teto da curva
  bonusToday: number;       // extra liberado manualmente hoje
  effectiveLimit: number;   // teto que realmente vale agora (curva + bônus)
  unlimitedToday: boolean;  // "disparar todos" ativado hoje
  atCap: boolean;           // bateu o teto efetivo
  sentToday: number;
  remaining: number;
  nextLimit: number | null;
  plateauLimit: number;
  curve: number[];
}

export type DispatchJobStatus = "Pending" | "Sent" | "Failed" | "Skipped";

export interface DispatchReportItem {
  phone: string | null;
  name: string | null;
  status: DispatchJobStatus;
  scheduledAt: string;
  sentAt: string | null;
  errorReason: string | null;
}

export interface DispatchJob {
  id: string;
  contactId: string;
  templateId: string;
  status: "Pending" | "Sent" | "Failed" | "Skipped";
  scheduledAt: string;
  sentAt: string | null;
  errorReason: string | null;
}
