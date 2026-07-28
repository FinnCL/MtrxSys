export type WahaStatus = "Unknown" | "Stopped" | "Starting" | "ScanQrCode" | "Working" | "Failed";

// Janela de reassentamento (settle): quanto falta, após o chip (re)conectar, pra liberar o envio
// manual. remainingSeconds decrementa; ready=true quando já pode enviar. working=false = sem sessão.
export interface WahaReadiness {
  working: boolean;
  settleSeconds: number;
  remainingSeconds: number;
  ready: boolean;
}

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
  c: Pick<Contact, "stage" | "optOutAt" | "lastSentAt" | "sentElsewhere">,
): ContactStatusBadge {
  if (c.optOutAt) return { label: "Saiu", className: "stage-lost" };
  if (c.stage === "Lead" && c.lastSentAt) {
    return { label: "Não respondeu", className: "stage-no-reply" };
  }
  // Sem envio local, mas já consta no registro compartilhado → outro chip já disparou pra ele.
  if (c.stage === "Lead" && c.sentElsewhere) {
    return { label: "Enviado · outro chip", className: "stage-no-reply" };
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
  // Status de entrega (do ack do WhatsApp) pra mensagem NOSSA (outbound). "Failed" = o WhatsApp
  // rejeitou o envio (companion restrito / cold) — a mensagem "saiu" mas não chegou a ninguém.
  deliveryStatus: "None" | "Pending" | "Sent" | "Delivered" | "Read" | "Failed";
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
  // Consta no registro compartilhado (tratado por outro ambiente). Vem false quando o
  // recurso está desligado. Vira selo só quando não houve envio local (ver contactStatusBadge).
  sentElsewhere: boolean;
  // Chip que importou o contato (número). Null = legado/sem marca.
  importedByPhone: string | null;
  // true = importado pelo chip CONECTADO agora → pode disparar. false = de outro chip/legado →
  // FRIO pra este chip (o disparo pula, anti-463). O front mostra os false em CINZA com selo "outro chip".
  fromCurrentChip: boolean;
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

// Quem está DENTRO de um grupo. O backend já resolve o número real por trás do @lid.
export interface GroupMember {
  phone: string;
  name: string | null;
  isAdmin: boolean;
}

export interface ContactGroupTag {
  groupTag: string;
  count: number;
}

// Coletor de grupos: link de convite captado por busca, Telegram ou entrada manual.
export type GroupLinkStatus = "Found" | "Resolved" | "Invalid" | "Joined" | "Imported" | "Foreign";

export interface GroupLink {
  inviteCode: string;
  inviteUrl: string;
  sourceChannel: string | null;
  groupName: string | null;
  participantCount: number | null;
  status: GroupLinkStatus;
  matchedKeyword: string | null;
  whatsAppGroupId: string | null;
  collectedAt: string;
  resolvedAt: string | null;
}

// Página de links (paginação server-side da aba Coletor).
export interface GroupLinkPage {
  items: GroupLink[];
  total: number;
  limit: number;
  offset: number;
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

// Cadastro manual (digitar/colar números). Status por linha, na ordem em que foram colados.
export type ManualLineStatus = "Ok" | "Corrected" | "Duplicate" | "Invalid";

export interface ManualLineResult {
  input: string; // o que o usuário colou nessa linha
  status: ManualLineStatus;
  phone: string | null; // E.164 final (null quando inválido)
  correction: string | null; // rótulo da auto-correção (ex.: "9º dígito inserido")
  reason: string | null; // motivo quando inválido
}

export interface ManualImportResult {
  total: number;
  added: number;
  duplicated: number;
  corrected: number;
  invalid: number;
  lines: ManualLineResult[];
}

export interface GoogleContact {
  phone: string;
  name: string | null;
}

export interface GoogleInvalid {
  phone: string;
  motivo: string;
}

// Diferença entre a agenda Google do chip e o banco.
//
// TRÊS estados, não um booleano: "não consegui ler a conta" e "não há nada novo" produziriam telas
// idênticas se fossem o mesmo valor, e a primeira é uma pane que o operador precisa ver.
//   ok        · leitura feita, os números abaixo valem
//   desligado · não há provider Google configurado neste stack
//   ilegivel  · a conta não respondeu (token morto, rede, cota)
export interface GooglePreview {
  estado: "ok" | "desligado" | "ilegivel";
  naConta: number;
  jaNoSistema: number;
  // Conhecidos mas descartados ou com opt-out. Contados pra a soma fechar, NUNCA oferecidos.
  bloqueados: number;
  novos: GoogleContact[];
  invalidos: GoogleInvalid[];
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
  retrying: number;
  // Quantos da fila (Pending+Retrying) são do chip conectado agora — só esses saem (gate anti-463).
  // A UI desabilita "Iniciar envios" quando pending>0 mas este é 0 (fila 100% de outro chip).
  pendingFromCurrentChip: number;
}

// Estado do aquecimento do chip (vindo de /api/dispatch/warmup). day é base-1.
export interface WarmupStatus {
  phone: string | null;     // número que o aquecimento acompanha
  startedOn: string;
  // Fase "só quem respondeu": nos primeiros dias ATIVOS do chip, o disparo trava no público
  // "Respondeu" (frio num chip novo derruba). A tela usa pra travar o seletor de público.
  responderOnlyPhase: boolean;
  // Fase híbrida (dia 4 → platô): mistura círculo re-enviável + frios novos, intercalado. A tela usa
  // pra exibir a fila "Na fila" na ordem REAL de envio (intercalada), não agrupada por "Respondeu".
  hybridPhase: boolean;
  responderOnlyDaysLeft: number;
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

// FASE HUMANA — dias 1-3 de um chip novo. O disparo fica travado enquanto o operador conversa à mão
// pela aba Chat; abre sozinho quando bate evidência (gente que respondeu) E dias com atividade.
// Distinto do WarmupStatus acima: aquele é QUANTO pode sair por dia, este é SE já pode sair algo.
export interface HumanPhaseStatus {
  // false = a fase não vale pra este chip (recurso desligado, ou chip anterior à data de corte do
  // rollout). A UI não renderiza nada — é o caso de todo chip que já estava em produção.
  applies: boolean;
  startedOn?: string;
  satisfied?: boolean;
  activeDays?: number;   // dias COM atividade de saída (chip parado não conta)
  minDays?: number;
  // Conta QUALQUER conversa não-grupo com ida-e-volta, não só o círculo — é o que destrava o
  // disparo. O círculo abaixo é lista de checagem, não a fonte do gate.
  qualifiedPeople?: number;
  minPeople?: number;
  minInbound?: number;
  minOutbound?: number;
  // Robô conversando com o círculo pela via do Chat. Não burla a fase: ele produz saída, e o gate
  // exige resposta — se ninguém responder, a fase não fecha. Zera na troca de chip (decisão
  // consciente por chip).
  autoSendEnabled?: boolean;
  circle?: HumanPhasePerson[];
}

export interface HumanPhasePerson {
  phone: string;
  name: string | null;
  conversationId: string | null;  // null = ainda não há conversa com essa pessoa
  inbound: number;
  outbound: number;
  qualified: boolean;
}

// Contato da agenda do aparelho (via WAHA).
export interface PhoneContact {
  phone: string;
  name: string | null;
  isMyContact: boolean;  // salvo na agenda (vs. só apareceu num chat)
}

export interface CircleMember {
  id: string;
  phone: string;
  name: string | null;
}

export type DispatchJobStatus = "Pending" | "Sent" | "Failed" | "Skipped" | "Retrying";

export interface DispatchReportItem {
  phone: string | null;
  name: string | null;
  status: DispatchJobStatus;
  scheduledAt: string;
  sentAt: string | null;
  errorReason: string | null;
  attemptCount: number;
  // false = contato de OUTRO chip (o disparo pula, anti-463). O front mostra em cinza + "outro chip".
  // Legado (sem marca) vem true (não marca à toa).
  fromCurrentChip: boolean;
  // Contato já respondeu/avançou (Stage != Novo/Descartado). O relatório marca "Respondeu" na linha.
  engaged: boolean;
}

export interface DispatchJob {
  id: string;
  contactId: string;
  templateId: string;
  status: DispatchJobStatus;
  scheduledAt: string;
  sentAt: string | null;
  errorReason: string | null;
  attemptCount: number;
}

