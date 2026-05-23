import type { Conversation } from "../api/types";

export type ChatKind = "individual" | "group" | "lid" | "unknown";

export function classify(waChatId: string): ChatKind {
  if (waChatId.endsWith("@g.us")) return "group";
  if (waChatId.endsWith("@lid")) return "lid";
  if (waChatId.endsWith("@c.us") || waChatId.endsWith("@s.whatsapp.net")) return "individual";
  return "unknown";
}

export function isLinkedId(waChatId: string): boolean {
  return classify(waChatId) === "lid";
}

export function isGroup(waChatId: string): boolean {
  return classify(waChatId) === "group";
}

function rawDigits(waChatId: string): string {
  return waChatId.replace(/@[^@]+$/, "");
}

export function shortIdentifier(waChatId: string): string {
  const d = rawDigits(waChatId);
  return d.length > 8 ? d.slice(0, 6) + "…" : d;
}

export function formatPhone(waChatId: string): string {
  return "+" + rawDigits(waChatId);
}

export function conversationDisplayName(c: Pick<Conversation, "title" | "waChatId" | "isGroup">): string {
  if (c.title && c.title.trim()) return c.title;
  const kind = classify(c.waChatId);
  if (kind === "lid") return "Contato privado";
  if (kind === "group" || c.isGroup) return `Grupo ${shortIdentifier(c.waChatId)}`;
  return formatPhone(c.waChatId);
}

export function conversationSubtitle(c: Pick<Conversation, "waChatId" | "isGroup">): string {
  const kind = classify(c.waChatId);
  if (kind === "lid") return "privado";
  if (kind === "group" || c.isGroup) return "Grupo";
  return "1:1";
}

export function emptyContactPaneMessage(c: Pick<Conversation, "waChatId" | "isGroup">): string {
  const kind = classify(c.waChatId);
  if (kind === "lid") return "Contato privado — telefone oculto pelo WhatsApp";
  if (kind === "group" || c.isGroup) return "Grupo — sem contato 1:1";
  return "Sem contato vinculado";
}
