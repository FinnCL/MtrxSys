import { contactStatusBadge, type Contact, type DispatchJobStatus, type DispatchReportItem } from "../api/types";

export const DISPATCH_STATUS_LABELS: Record<DispatchJobStatus, string> = {
  Pending: "Na fila",
  Sent: "Enviada",
  Failed: "Falhou",
  Skipped: "Pulada",
  Retrying: "Reenviando",
};

/**
 * Monta uma planilha de uma aba a partir de linhas-objeto e dispara o download.
 *
 * 🔴 O `xlsx` entra por IMPORT DINÂMICO, e é isso que torna esta função async. Ele pesa ~437 KB
 * minificado — sozinho, a maior peça do bundle. Como quatro telas importavam este módulo de forma
 * estática (Contatos, Coletor, Grupos, Disparo), a biblioteca descia no carregamento inicial para
 * TODO usuário, inclusive quem nunca clica em baixar planilha. Aqui ela vira um chunk separado,
 * buscado só no clique.
 *
 * O `await import()` é resolvido pelo navegador contra o servidor: se o chunk não vier (rede caiu,
 * ou o deploy trocou os arquivos com a aba aberta), a promise REJEITA. Por isso as funções públicas
 * propagam o erro em vez de engolir — quem chama sabe onde mostrar a mensagem; um catch mudo aqui
 * viraria um botão que não baixa e não reclama.
 */
async function writeXlsx(
  rows: Record<string, string>[],
  colWidths: number[],
  sheetName: string,
  fileName: string,
): Promise<void> {
  const XLSX = await import("xlsx");
  const worksheet = XLSX.utils.json_to_sheet(rows);
  worksheet["!cols"] = colWidths.map((wch) => ({ wch }));
  const workbook = XLSX.utils.book_new();
  XLSX.utils.book_append_sheet(workbook, worksheet, sheetName);
  XLSX.writeFile(workbook, fileName);
}

const today = (): string => new Date().toISOString().slice(0, 10);
const fmt = (iso: string | null): string => (iso ? new Date(iso).toLocaleString() : "");

/**
 * Gera e baixa uma planilha .xlsx com os contatos informados:
 * nome, telefone, grupo de origem, status, quando foi salvo e quando foi importado.
 */
export async function downloadContactsXlsx(contacts: Contact[], groupName: string): Promise<void> {
  const importedAt = new Date().toLocaleString();
  const rows = contacts.map((c) => ({
    Nome: c.name ?? "",
    Telefone: c.phoneE164,
    Grupo: c.groupTag ?? groupName,
    Status: contactStatusBadge(c).label,
    "Salvo em": fmt(c.optInAt),
    "Importado em": importedAt,
  }));

  const safeName = groupName.replace(/[^\w-]+/g, "_").slice(0, 40) || "grupo";
  await writeXlsx(rows, [24, 18, 28, 14, 20, 20], "Contatos", `contatos-${safeName}-${today()}.xlsx`);
}

/**
 * Gera e baixa o relatório de envios em .xlsx, com o status de cada contato
 * (Enviada / Falhou / Pulada / Na fila), horário e motivo do erro.
 */
export async function downloadDispatchReportXlsx(items: DispatchReportItem[]): Promise<void> {
  const rows = items.map((i) => ({
    Telefone: i.phone ?? "",
    Nome: i.name ?? "",
    Status: DISPATCH_STATUS_LABELS[i.status] ?? i.status,
    "Agendado em": fmt(i.scheduledAt),
    "Enviado em": fmt(i.sentAt),
    Erro: i.errorReason ?? "",
  }));

  await writeXlsx(rows, [18, 24, 12, 20, 20, 40], "Envios", `relatorio-envios-${today()}.xlsx`);
}
