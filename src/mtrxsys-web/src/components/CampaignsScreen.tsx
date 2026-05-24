import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type {
  ContactGroupTag,
  DispatchJobStatus,
  DispatchReportItem,
  DispatchStats,
  MessageTemplate,
} from "../api/types";
import { DISPATCH_STATUS_LABELS, downloadDispatchReportXlsx } from "../utils/exportContacts";

// Texto inicial do campo: já traz a saudação em spintax e a linha de saída (SAIR),
// que deve estar sempre presente. O usuário escreve o miolo da mensagem no meio.
const DEFAULT_DRAFT =
  "{Oi|Olá|E aí}, {tudo bem|tudo certo}? {Tenho uma novidade|Queria te mostrar uma coisa} {pra você|que pode te interessar}.\n\nResponda SAIR para não receber mais mensagens.";

const STAT_CHIPS: { key: DispatchJobStatus; label: string; cls: string }[] = [
  { key: "Pending", label: "Na fila", cls: "stat-pending" },
  { key: "Sent", label: "Enviadas", cls: "stat-sent" },
  { key: "Failed", label: "Falharam", cls: "stat-failed" },
  { key: "Skipped", label: "Puladas", cls: "stat-skipped" },
];

export function CampaignsScreen() {
  const [messages, setMessages] = useState<MessageTemplate[]>([]);
  const [groupTags, setGroupTags] = useState<ContactGroupTag[]>([]);
  // Mensagens DESmarcadas (excluídas do rodízio). Por padrão todas participam.
  const [excludedIds, setExcludedIds] = useState<Set<string>>(new Set());
  const [stats, setStats] = useState<DispatchStats | null>(null);
  const [report, setReport] = useState<DispatchReportItem[]>([]);
  const [reportStatus, setReportStatus] = useState<"" | DispatchJobStatus>("");
  const [draft, setDraft] = useState(DEFAULT_DRAFT);
  const [audience, setAudience] = useState<"all" | "responded">("all");
  const [group, setGroup] = useState("");
  const [adding, setAdding] = useState(false);
  const [dispatching, setDispatching] = useState(false);
  const [dispatchMsg, setDispatchMsg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  const reportStatusRef = useRef<"" | DispatchJobStatus>("");
  reportStatusRef.current = reportStatus;

  // Dados estáticos (mudam só quando você cria/remove mensagem ou importa grupo).
  const loadLists = useCallback(async () => {
    try {
      const [t, g] = await Promise.all([api.listTemplates(), api.listContactGroupTags()]);
      setMessages(t.filter((x) => x.active));
      setGroupTags(g);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }, []);

  // Dados "ao vivo" (mudam conforme o dispatcher processa) — atualizados no timer.
  const loadLive = useCallback(async () => {
    try {
      const [s, r] = await Promise.all([
        api.dispatchStats(),
        api.dispatchReport(reportStatusRef.current || undefined),
      ]);
      setStats(s);
      setReport(r);
      setError(null);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }, []);

  useEffect(() => {
    void loadLists();
    void loadLive();
    const handle = setInterval(loadLive, 5_000);
    return () => clearInterval(handle);
  }, [loadLists, loadLive]);

  // Reaplica o filtro do relatório imediatamente quando muda o status selecionado.
  useEffect(() => {
    void loadLive();
  }, [reportStatus, loadLive]);

  function countFor(key: DispatchJobStatus): number {
    if (!stats) return 0;
    return key === "Pending" ? stats.pending
      : key === "Sent" ? stats.sent
      : key === "Failed" ? stats.failed
      : stats.skipped;
  }

  async function addMessage() {
    const text = draft.trim();
    if (!text) return;
    setAdding(true);
    try {
      await api.createTemplate(text, "Greeting");
      setDraft(DEFAULT_DRAFT);
      await loadLists();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setAdding(false);
    }
  }

  async function removeMessage(id: string) {
    try {
      await api.deleteTemplate(id);
      await loadLists();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  function toggleExclude(id: string) {
    setExcludedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  const selectedIds = messages.filter((m) => !excludedIds.has(m.id)).map((m) => m.id);

  async function onDispatch() {
    if (selectedIds.length === 0) {
      setDispatchMsg("Marque ao menos uma mensagem antes de disparar.");
      return;
    }
    setDispatching(true);
    setDispatchMsg(null);
    try {
      const result = await api.dispatch(selectedIds, {
        engagedOnly: audience === "responded" ? true : undefined,
        groupTag: group.trim() || undefined,
      });
      setDispatchMsg(
        result.scheduled > 0
          ? `${result.scheduled} mensagens na fila, sorteadas entre suas ${result.templatesUsed} mensagem(ns). Vão sair aos poucos, com intervalos.`
          : "Nenhum contato encontrado pra esse público. Importe contatos ou mude o filtro.",
      );
      await loadLive();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    } finally {
      setDispatching(false);
    }
  }

  const sendLabel = dispatching
    ? "Enviando..."
    : audience === "responded"
      ? "Disparar pros que responderam"
      : group.trim()
        ? "Disparar pro grupo"
        : "Disparar pra todos";

  return (
    <main className="campaigns-screen">
      <header className="campaigns-section">
        <h2>Disparo de mensagens</h2>
        <p className="muted">
          Monte um conjunto de mensagens, escolha pra quem, e dispare. Cada contato recebe uma sorteada —
          sem texto repetido. O envio é automático e espaçado.
        </p>
      </header>

      {error && <p className="error">{error}</p>}

      {stats && (
        <div className="dispatch-stats">
          {STAT_CHIPS.map((c) => (
            <button
              key={c.key}
              type="button"
              className={`stat-chip ${c.cls} chip-btn${reportStatus === c.key ? " active" : ""}`}
              onClick={() => setReportStatus(reportStatus === c.key ? "" : c.key)}
              title="Clique para ver/filtrar esses contatos abaixo"
            >
              {c.label}: {countFor(c.key)}
            </button>
          ))}
        </div>
      )}

      <section className="campaigns-section">
        <h3>1 · Suas mensagens ({messages.length})</h3>
        <p className="muted small">
          <strong>Como variar sem cair como spam:</strong> dentro de uma mensagem, use{" "}
          <code>{"{a|b|c}"}</code> — o sistema sorteia uma opção por contato (pode usar vários pontos no
          texto, ex.: <code>{"{Oi|Olá}"}</code>). <strong>Não</strong> separe a saudação em modelos
          diferentes ("Oi" num, "Olá" noutro): isso manda texto idêntico pra muita gente. Só crie um{" "}
          <strong>2º modelo</strong> se o <strong>corpo</strong> da mensagem for diferente (outro
          argumento), não pra trocar a saudação. Seus contatos não têm nome → prefira saudações sem nome.
          A linha de saída (<strong>"SAIR"</strong>) já vem pronta no campo — mantenha ela.
        </p>
        {messages.length > 0 && (
          <>
            <p className="muted small pool-label">
              Salvas — marque quais entram no rodízio ({selectedIds.length} de {messages.length} selecionadas):
            </p>
            <ul className="message-pool">
              {messages.map((m) => {
                const selected = !excludedIds.has(m.id);
                return (
                  <li key={m.id} className={selected ? undefined : "unselected"}>
                    <input
                      type="checkbox"
                      className="message-check"
                      checked={selected}
                      onChange={() => toggleExclude(m.id)}
                      title={selected ? "No rodízio (desmarque para excluir)" : "Fora do rodízio"}
                    />
                    <span className="message-pool-text">{m.contentSpintax}</span>
                    <button
                      type="button"
                      className="message-remove"
                      title="Remover esta mensagem"
                      onClick={() => void removeMessage(m.id)}
                    >
                      ×
                    </button>
                  </li>
                );
              })}
            </ul>
          </>
        )}
        <div className="message-add">
          <label className="message-add-label" htmlFor="new-message">
            Escrever nova mensagem {messages.length > 0 ? "(adiciona outra ao rodízio)" : ""}
          </label>
          <textarea
            id="new-message"
            className="message-box"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="Escreva uma mensagem (ex.: {Oi|Olá}! ... responda SAIR pra não receber.)"
            rows={4}
          />
          <button type="button" onClick={() => void addMessage()} disabled={adding || !draft.trim()}>
            {adding ? "Adicionando..." : "+ Adicionar ao rodízio"}
          </button>
        </div>
      </section>

      <section className="campaigns-section">
        <h3>2 · Para quem enviar</h3>
        <div className="audience-row">
          <label>
            <span>Público</span>
            <select value={audience} onChange={(e) => setAudience(e.target.value as "all" | "responded")}>
              <option value="all">Todos os contatos</option>
              <option value="responded">Só quem já respondeu</option>
            </select>
          </label>
          <label>
            <span>Grupo (opcional)</span>
            <select value={group} onChange={(e) => setGroup(e.target.value)}>
              <option value="">Todos os grupos</option>
              {groupTags.map((g) => (
                <option key={g.groupTag} value={g.groupTag}>
                  {g.groupTag} ({g.count})
                </option>
              ))}
            </select>
          </label>
        </div>
        <p className="muted small">
          "Só quem já respondeu" envia apenas pros contatos engajados — mais resultado e mais seguro. Quem
          pediu pra sair nunca recebe.
        </p>
      </section>

      <section className="campaigns-section">
        <h3>3 · Disparar</h3>
        <button
          type="button"
          className="dispatch-btn"
          onClick={() => void onDispatch()}
          disabled={dispatching || selectedIds.length === 0}
        >
          {sendLabel}
        </button>
        {dispatchMsg && <p className="dispatch-msg">{dispatchMsg}</p>}
      </section>

      <section className="campaigns-section">
        <div className="report-head">
          <h3>
            Resultado dos envios{reportStatus ? ` · ${DISPATCH_STATUS_LABELS[reportStatus]}` : ""}
          </h3>
          <button
            type="button"
            className="report-export"
            onClick={() => downloadDispatchReportXlsx(report)}
            disabled={report.length === 0}
          >
            Baixar relatório (Excel)
          </button>
        </div>
        <p className="muted small">
          {reportStatus
            ? "Mostrando só os contatos desse status. Clique no chip de novo pra ver todos."
            : "Mostrando todos os envios. Clique num chip de status acima pra filtrar."}
        </p>
        {report.length === 0 ? (
          <p className="muted">Nenhum envio ainda.</p>
        ) : (
          <table className="contacts-table">
            <thead>
              <tr>
                <th>Telefone</th>
                <th>Nome</th>
                <th>Status</th>
                <th>Quando</th>
                <th>Erro</th>
              </tr>
            </thead>
            <tbody>
              {report.map((i, idx) => (
                <tr key={idx}>
                  <td className="mono">{i.phone ?? "—"}</td>
                  <td>{i.name || <span className="muted">—</span>}</td>
                  <td>
                    <span className={`stat-chip stat-${i.status.toLowerCase()}`}>
                      {DISPATCH_STATUS_LABELS[i.status]}
                    </span>
                  </td>
                  <td>{new Date(i.sentAt ?? i.scheduledAt).toLocaleString()}</td>
                  <td className="muted small">{i.errorReason ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </section>
    </main>
  );
}
