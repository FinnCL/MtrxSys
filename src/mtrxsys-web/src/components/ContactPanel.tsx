import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api/client";
import { ALL_STAGES, STAGE_LABELS, type ContactDetail, type Stage } from "../api/types";

interface Props {
  contactId: string;
}

export function ContactPanel({ contactId }: Props) {
  const [data, setData] = useState<ContactDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [noteDraft, setNoteDraft] = useState("");
  const [newTag, setNewTag] = useState("");
  const [savingStage, setSavingStage] = useState(false);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const detail = await api.getContact(contactId);
        if (!cancelled) {
          setData(detail);
          setError(null);
        }
      } catch (ex) {
        if (!cancelled) setError(ex instanceof Error ? ex.message : String(ex));
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [contactId]);

  async function changeStage(newStage: Stage) {
    if (!data) return;
    setSavingStage(true);
    try {
      await api.patchContact(contactId, { stage: newStage });
      const refreshed = await api.getContact(contactId);
      setData(refreshed);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setSavingStage(false);
    }
  }

  async function addNote(e: FormEvent) {
    e.preventDefault();
    const body = noteDraft.trim();
    if (!body) return;
    try {
      await api.addNote(contactId, body);
      const refreshed = await api.getContact(contactId);
      setData(refreshed);
      setNoteDraft("");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  async function addTag(name: string) {
    if (!name.trim()) return;
    try {
      await api.patchContact(contactId, { addTags: [name.trim()] });
      const refreshed = await api.getContact(contactId);
      setData(refreshed);
      setNewTag("");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  async function removeTag(name: string) {
    try {
      await api.patchContact(contactId, { removeTags: [name] });
      const refreshed = await api.getContact(contactId);
      setData(refreshed);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  if (error) return <aside className="contact-panel"><p className="error">{error}</p></aside>;
  if (!data) return <aside className="contact-panel"><p>Carregando...</p></aside>;

  return (
    <aside className="contact-panel">
      <h2>{data.contact.name ?? data.contact.phoneE164}</h2>
      <div className="contact-phone">{data.contact.phoneE164}</div>

      <section className="panel-section">
        <h3>Funil</h3>
        <div className="stage-picker">
          {ALL_STAGES.map((s) => (
            <button
              key={s}
              type="button"
              disabled={savingStage}
              className={`stage-chip${data.contact.stage === s ? " active" : ""}`}
              onClick={() => changeStage(s)}
            >
              {STAGE_LABELS[s]}
            </button>
          ))}
        </div>
        {data.contact.stageChangedAt && (
          <div className="stage-meta">
            Mudou em {new Date(data.contact.stageChangedAt).toLocaleString()}
          </div>
        )}
      </section>

      <section className="panel-section">
        <h3>Tags</h3>
        <div className="tags">
          {data.tags.map((t) => (
            <span key={t} className="tag-chip">
              {t}
              <button type="button" onClick={() => void removeTag(t)} title="remover">×</button>
            </span>
          ))}
          {data.tags.length === 0 && <span className="muted">—</span>}
        </div>
        <form
          className="tag-add"
          onSubmit={(e) => {
            e.preventDefault();
            void addTag(newTag);
          }}
        >
          <input
            value={newTag}
            onChange={(e) => setNewTag(e.target.value)}
            placeholder="nova tag"
          />
          <button type="submit">+</button>
        </form>
      </section>

      <section className="panel-section">
        <h3>Notas</h3>
        <ul className="notes">
          {data.notes.map((n) => (
            <li key={n.id}>
              <div className="note-body">{n.body}</div>
              <div className="note-meta">{new Date(n.createdAt).toLocaleString()}</div>
            </li>
          ))}
          {data.notes.length === 0 && <li className="muted">Nenhuma nota</li>}
        </ul>
        <form className="note-add" onSubmit={addNote}>
          <textarea
            value={noteDraft}
            onChange={(e) => setNoteDraft(e.target.value)}
            placeholder="Adicionar nota"
            rows={2}
          />
          <button type="submit" disabled={!noteDraft.trim()}>
            Salvar
          </button>
        </form>
      </section>

      {data.stageHistory.length > 0 && (
        <section className="panel-section">
          <h3>Histórico</h3>
          <ul className="history">
            {data.stageHistory.map((c) => (
              <li key={c.id}>
                <span>{c.fromStage ? STAGE_LABELS[c.fromStage] : "(novo)"} → {STAGE_LABELS[c.toStage]}</span>
                <span className="muted">{new Date(c.changedAt).toLocaleString()}</span>
              </li>
            ))}
          </ul>
        </section>
      )}
    </aside>
  );
}
