import { useEffect, useMemo, useState } from "react";
import { api } from "../api/client";
import type { Contact, Conversation, PhoneContact } from "../api/types";

interface Props {
  onClose: () => void;
  // Chamado com a conversa criada, pra o Chat já abrir a thread.
  onStarted: (c: Conversation) => void;
}

type Source = "agenda" | "crm" | "number";

// Alvo escolhido: por telefone (agenda/digitado) OU por contato do CRM (id). `key` é o id estável de
// seleção (telefone ou id do contato); o label é só pra exibir.
interface Target {
  key: string;
  phone?: string;
  contactId?: string;
  label: string;
}

// Modal de "Nova conversa": escolhe o destinatário (agenda do aparelho, contato do CRM, ou número
// digitado) e manda a 1ª mensagem. O backend barra número sem WhatsApp / opt-out / sessão fora.
export function StartConversationModal({ onClose, onStarted }: Props) {
  const [source, setSource] = useState<Source>("agenda");
  const [phoneList, setPhoneList] = useState<PhoneContact[] | null>(null);
  const [crmList, setCrmList] = useState<Contact[] | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [search, setSearch] = useState("");
  const [target, setTarget] = useState<Target | null>(null);
  const [typed, setTyped] = useState("");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") onClose();
    };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  // Carrega a fonte escolhida sob demanda (uma vez cada). Trocar de aba limpa o alvo selecionado.
  useEffect(() => {
    setTarget(null);
    let cancelled = false;
    async function load() {
      setLoadError(null);
      try {
        if (source === "agenda" && phoneList === null) {
          const list = await api.phoneContacts();
          if (!cancelled) setPhoneList(list);
        } else if (source === "crm" && crmList === null) {
          const list = await api.listContacts();
          if (!cancelled) setCrmList(list);
        }
      } catch (ex) {
        if (!cancelled) setLoadError(ex instanceof Error ? ex.message : String(ex));
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [source, phoneList, crmList]);

  const filtered = useMemo(() => {
    const q = search.trim().toLowerCase();
    if (source === "agenda") {
      const list = phoneList ?? [];
      return (q
        ? list.filter((c) => (c.name ?? "").toLowerCase().includes(q) || c.phone.includes(q))
        : list
      ).map((c) => ({ sub: c.phone, target: { key: c.phone, phone: c.phone, label: c.name ?? c.phone } as Target }));
    }
    const list = crmList ?? [];
    return (q
      ? list.filter((c) => (c.name ?? "").toLowerCase().includes(q) || c.phoneE164.includes(q))
      : list
    ).map((c) => ({ sub: c.phoneE164, target: { key: c.id, contactId: c.id, label: c.name ?? c.phoneE164 } as Target }));
  }, [source, search, phoneList, crmList]);

  const activeTarget: Target | null =
    source === "number"
      ? (typed.trim() ? { key: typed.trim(), phone: typed.trim(), label: typed.trim() } : null)
      : target;
  const canSubmit = !busy && text.trim().length > 0 && activeTarget !== null;

  async function submit() {
    if (!canSubmit || !activeTarget) return;
    setBusy(true);
    setError(null);
    try {
      const body = activeTarget.contactId
        ? { contactId: activeTarget.contactId, text }
        : { phone: activeTarget.phone, text };
      const result = await api.startConversation(body);
      onStarted(result.conversation);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setBusy(false);
    }
  }

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="modal-dialog" role="dialog" aria-modal="true" onClick={(e) => e.stopPropagation()}>
        <h3>Nova conversa</h3>
        <div className="modal-body">
          <div className="tabs conv-tabs">
            <button type="button" className={`tab-btn${source === "agenda" ? " active" : ""}`} onClick={() => setSource("agenda")}>
              Agenda do aparelho
            </button>
            <button type="button" className={`tab-btn${source === "crm" ? " active" : ""}`} onClick={() => setSource("crm")}>
              Contatos do CRM
            </button>
            <button type="button" className={`tab-btn${source === "number" ? " active" : ""}`} onClick={() => setSource("number")}>
              Digitar número
            </button>
          </div>

          {source === "number" ? (
            <input
              className="conv-search"
              type="tel"
              value={typed}
              onChange={(e) => setTyped(e.target.value)}
              placeholder="+55 71 99999-8888"
              disabled={busy}
            />
          ) : (
            <>
              <input
                className="conv-search"
                type="search"
                value={search}
                onChange={(e) => setSearch(e.target.value)}
                placeholder="Buscar nome ou telefone…"
              />
              {loadError && <p className="error">{loadError}</p>}
              <ul className="start-conv-list">
                {filtered.length === 0 && !loadError && (
                  <li className="muted small">Nada aqui — troque a fonte ou digite o número.</li>
                )}
                {filtered.slice(0, 200).map((item) => (
                  <li
                    key={item.target.key}
                    className={target?.key === item.target.key ? "selected" : undefined}
                    onClick={() => setTarget(item.target)}
                  >
                    <span className="start-conv-name">{item.target.label}</span>
                    <span className="muted small mono">{item.sub}</span>
                  </li>
                ))}
              </ul>
            </>
          )}

          <p className="muted small">
            Para <strong>{activeTarget ? activeTarget.label : "—"}</strong>. Aquecimento de chip novo:
            prefira mandar do próprio aparelho e sincronizar — enviar por aqui usa o WAHA.
          </p>
          <textarea
            className="manual-numbers-input"
            rows={4}
            value={text}
            onChange={(e) => setText(e.target.value)}
            placeholder="Oi! Tudo bem contigo?"
            disabled={busy}
          />
          {error && <p className="error">{error}</p>}
        </div>
        <div className="modal-actions">
          <button type="button" className="btn-ghost" onClick={onClose}>
            Cancelar
          </button>
          <button type="button" onClick={() => void submit()} disabled={!canSubmit}>
            {busy ? "Enviando..." : "Enviar 1ª mensagem"}
          </button>
        </div>
      </div>
    </div>
  );
}
