import { useEffect, useState, type FormEvent } from "react";
import { api } from "../api/client";
import type { ContactDetail, Stage } from "../api/types";
import { ConfirmDialog } from "./ConfirmDialog";

interface Props {
  contactId: string;
}

// Rótulo de status alinhado às abas do Chat. É só-leitura: o sistema classifica sozinho
// (respondeu → Respondeu; "SAIR" → Saiu). Sem funil manual pra evitar cliques/ruído.
const STATUS_LABEL: Record<Stage, string> = {
  Lead: "Sem resposta",
  Qualified: "Respondeu",
  Proposal: "Negociando",
  Won: "Cliente",
  Lost: "Descartado",
};

export function ContactPanel({ contactId }: Props) {
  const [data, setData] = useState<ContactDetail | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [noteDraft, setNoteDraft] = useState("");
  const [confirmReactivate, setConfirmReactivate] = useState(false);

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

  async function refresh() {
    setData(await api.getContact(contactId));
  }

  async function reactivate() {
    setConfirmReactivate(false);
    try {
      await api.reactivateContact(contactId);
      await refresh();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  async function addNote(e: FormEvent) {
    e.preventDefault();
    const body = noteDraft.trim();
    if (!body) return;
    try {
      await api.addNote(contactId, body);
      await refresh();
      setNoteDraft("");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  if (error) return <aside className="contact-panel"><p className="error">{error}</p></aside>;
  if (!data) return <aside className="contact-panel"><p>Carregando...</p></aside>;

  const optedOut = data.contact.optOutAt != null;

  return (
    <aside className="contact-panel">
      <h2>{data.contact.name ?? data.contact.phoneE164}</h2>
      <div className="contact-phone">{data.contact.phoneE164}</div>

      <section className="panel-section">
        <h3>Status</h3>
        <div className="status-readonly">
          <span className={`stage-badge stage-${(optedOut ? "lost" : data.contact.stage.toLowerCase())}`}>
            {optedOut ? "Saiu" : STATUS_LABEL[data.contact.stage]}
          </span>
          {optedOut && (
            <button
              type="button"
              className="reactivate-btn"
              title="Religar: volta a receber mensagens"
              onClick={() => setConfirmReactivate(true)}
            >
              Reativar
            </button>
          )}
        </div>
        {data.contact.stageChangedAt && (
          <div className="stage-meta">
            Atualizado em {new Date(data.contact.stageChangedAt).toLocaleString()}
          </div>
        )}
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

      {confirmReactivate && (
        <ConfirmDialog
          title="Reativar contato?"
          message={
            <>
              Este contato pediu para <strong>sair</strong> (opt-out). Ao reativar, ele volta para a
              base e <strong>poderá receber mensagens de disparo novamente</strong>.
              <br />
              <br />
              Tem certeza de que deseja fazer isso?
            </>
          }
          confirmLabel="Sim, reativar"
          cancelLabel="Cancelar"
          danger
          onConfirm={() => void reactivate()}
          onCancel={() => setConfirmReactivate(false)}
        />
      )}
    </aside>
  );
}
