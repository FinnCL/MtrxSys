import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api/client";
import { ALL_STAGES, type DispatchStats, type MessageTemplate, type Stage } from "../api/types";

export function CampaignsScreen() {
  const [templates, setTemplates] = useState<MessageTemplate[]>([]);
  const [stats, setStats] = useState<DispatchStats | null>(null);
  const [newContent, setNewContent] = useState(
    "{Oi|Olá|E aí}, {{name|amigo}}! {Como vai|Tudo bem|Td bem}? Quero te apresentar uma novidade.",
  );
  const [creating, setCreating] = useState(false);
  const [selectedTemplate, setSelectedTemplate] = useState<string>("");
  const [filterStage, setFilterStage] = useState<Stage | "">("");
  const [filterTag, setFilterTag] = useState("");
  const [filterGroup, setFilterGroup] = useState("");
  const [dispatching, setDispatching] = useState(false);
  const [dispatchMsg, setDispatchMsg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);

  async function loadAll() {
    try {
      const [t, s] = await Promise.all([api.listTemplates(), api.dispatchStats()]);
      setTemplates(t);
      setStats(s);
      if (!selectedTemplate && t.length > 0) {
        setSelectedTemplate(t[0].id);
      }
      setError(null);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  useEffect(() => {
    void loadAll();
    const handle = setInterval(loadAll, 5_000);
    return () => clearInterval(handle);
  }, []);

  async function onCreate(e: FormEvent) {
    e.preventDefault();
    if (!newContent.trim()) return;
    setCreating(true);
    try {
      const created = await api.createTemplate(newContent.trim(), "Greeting");
      setTemplates((prev) => [...prev, created]);
      setSelectedTemplate(created.id);
      setNewContent("");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setCreating(false);
    }
  }

  async function onDispatch() {
    if (!selectedTemplate) {
      setDispatchMsg("Selecione um template");
      return;
    }
    setDispatching(true);
    setDispatchMsg(null);
    try {
      const result = await api.dispatch(selectedTemplate, {
        stage: filterStage || undefined,
        tagName: filterTag.trim() || undefined,
        groupTag: filterGroup.trim() || undefined,
      });
      setDispatchMsg(`${result.scheduled} envios agendados`);
      await loadAll();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    } finally {
      setDispatching(false);
      setTimeout(() => setDispatchMsg(null), 8000);
    }
  }

  return (
    <main className="campaigns-screen">
      <section className="campaigns-section">
        <h2>Campanhas</h2>
        {error && <p className="error">{error}</p>}
        {stats && (
          <div className="dispatch-stats">
            <span className="stat-chip stat-pending">Pendentes: {stats.pending}</span>
            <span className="stat-chip stat-sent">Enviados: {stats.sent}</span>
            <span className="stat-chip stat-failed">Falhas: {stats.failed}</span>
            <span className="stat-chip stat-skipped">Pulados: {stats.skipped}</span>
          </div>
        )}
      </section>

      <section className="campaigns-section">
        <h3>Novo template</h3>
        <p className="muted small">
          Use <code>{"{a|b|c}"}</code> pra variações (Spintax) e <code>{"{{name|amigo}}"}</code> pra
          substituições. Suportados: {"{{name}}, {{phone}}, {{group}}, {{theme}}"}.
        </p>
        <form className="template-form" onSubmit={onCreate}>
          <textarea
            value={newContent}
            onChange={(e) => setNewContent(e.target.value)}
            placeholder="Texto do template com Spintax"
            rows={4}
          />
          <button type="submit" disabled={creating || !newContent.trim()}>
            {creating ? "Criando..." : "Criar template"}
          </button>
        </form>
      </section>

      <section className="campaigns-section">
        <h3>Disparar</h3>
        <div className="dispatch-form">
          <label>
            <span>Template</span>
            <select value={selectedTemplate} onChange={(e) => setSelectedTemplate(e.target.value)}>
              <option value="">Selecione...</option>
              {templates.map((t) => (
                <option key={t.id} value={t.id}>
                  [{t.slot}] {t.contentSpintax.slice(0, 60)}
                  {t.contentSpintax.length > 60 ? "…" : ""}
                </option>
              ))}
            </select>
          </label>
          <div className="filter-row">
            <label>
              <span>Stage</span>
              <select value={filterStage} onChange={(e) => setFilterStage(e.target.value as Stage | "")}>
                <option value="">(todos)</option>
                {ALL_STAGES.map((s) => (
                  <option key={s} value={s}>{s}</option>
                ))}
              </select>
            </label>
            <label>
              <span>Tag</span>
              <input
                value={filterTag}
                onChange={(e) => setFilterTag(e.target.value)}
                placeholder="(todas)"
              />
            </label>
            <label>
              <span>Grupo</span>
              <input
                value={filterGroup}
                onChange={(e) => setFilterGroup(e.target.value)}
                placeholder="(todos)"
              />
            </label>
          </div>
          <button type="button" onClick={() => void onDispatch()} disabled={dispatching || !selectedTemplate}>
            {dispatching ? "Agendando..." : "Disparar"}
          </button>
          {dispatchMsg && <p className="dispatch-msg">{dispatchMsg}</p>}
        </div>
      </section>

      <section className="campaigns-section">
        <h3>Templates existentes ({templates.length})</h3>
        {templates.length === 0 ? (
          <p className="muted">Nenhum template criado ainda.</p>
        ) : (
          <ul className="template-list">
            {templates.map((t) => (
              <li key={t.id} className={`template-item${t.active ? "" : " inactive"}`}>
                <div className="template-meta">
                  <span className={`stat-chip stat-${t.slot.toLowerCase()}`}>{t.slot}</span>
                  {!t.active && <span className="muted small">inativo</span>}
                </div>
                <div className="template-content">{t.contentSpintax}</div>
              </li>
            ))}
          </ul>
        )}
      </section>
    </main>
  );
}
