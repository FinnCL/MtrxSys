import { contactStatusBadge, type Contact } from "../api/types";

/**
 * Selo de status do contato, reutilizado na tabela de Contatos e no painel.
 * Quando é "Não respondeu", o tooltip mostra a data do disparo.
 */
export function StatusBadge({ contact }: { contact: Contact }) {
  const { label, className } = contactStatusBadge(contact);
  const awaitingReply = !contact.optOutAt && contact.stage === "Lead" && contact.lastSentAt;
  const title = awaitingReply
    ? `Disparo enviado em ${new Date(contact.lastSentAt!).toLocaleDateString("pt-BR")} — sem resposta até agora`
    : undefined;

  return (
    <span className={`stage-badge ${className}`} title={title}>
      {label}
    </span>
  );
}
