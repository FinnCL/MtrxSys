import { useEffect, useState } from "react";
import { api } from "../api/client";
import { type Contact, type ContactGroupTag } from "../api/types";
import { ConfirmDialog } from "./ConfirmDialog";
import { StatusBadge } from "./StatusBadge";

export function ContactsScreen() {
  const [groups, setGroups] = useState<ContactGroupTag[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [contactsByGroup, setContactsByGroup] = useState<Record<string, Contact[]>>({});
  const [loadingGroup, setLoadingGroup] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [confirmTarget, setConfirmTarget] = useState<{ id: string; tag: string; label: string } | null>(null);
  // Ação de manutenção pendente de confirmação (proteção anti-miss-click via modal).
  const [pending, setPending] = useState<
    | { kind: "revert"; id: string; tag: string; label: string }
    | { kind: "delete"; tag: string }
    | null
  >(null);
  const [busy, setBusy] = useState(false);
  const [actionMsg, setActionMsg] = useState<string | null>(null);

  useEffect(() => {
    void (async () => {
      setLoading(true);
      try {
        setGroups(await api.listContactGroupTags());
        setError(null);
      } catch (ex) {
        setError(ex instanceof Error ? ex.message : String(ex));
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  async function reactivate(id: string, tag: string) {
    setConfirmTarget(null);
    try {
      await api.reactivateContact(id);
      const list = await api.listContacts({ groupTag: tag });
      setContactsByGroup((prev) => ({ ...prev, [tag]: list }));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  // Executa a ação de manutenção só após confirmação no modal (anti-miss-click).
  async function confirmPending() {
    if (!pending) return;
    setBusy(true);
    setError(null);
    try {
      if (pending.kind === "revert") {
        await api.patchContact(pending.id, { stage: "Lead" });
        setActionMsg(`"${pending.label}" não está mais como "Respondeu".`);
        const listNow = await api.listContacts({ groupTag: pending.tag });
        setContactsByGroup((prev) => ({ ...prev, [pending.tag]: listNow }));
      } else {
        const r = await api.deleteGroupContacts(pending.tag);
        setActionMsg(`${r.deleted} contato(s) do grupo "${pending.tag}" excluído(s).`);
        // O grupo fica vazio → some da lista; limpa o cache local e recarrega os grupos.
        const tag = pending.tag;
        setExpanded((e) => (e === tag ? null : e));
        setContactsByGroup((prev) => {
          const next = { ...prev };
          delete next[tag];
          return next;
        });
        setGroups(await api.listContactGroupTags());
      }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setBusy(false);
      setPending(null);
    }
  }

  async function toggle(tag: string) {
    if (expanded === tag) {
      setExpanded(null);
      return;
    }
    setExpanded(tag);
    // Sempre rebusca ao abrir (sem cache antigo), pra refletir status atualizado
    // — ex.: contato que acabou de responder "sair" já aparece como Descartado.
    setLoadingGroup(tag);
    try {
      const list = await api.listContacts({ groupTag: tag });
      setContactsByGroup((prev) => ({ ...prev, [tag]: list }));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setLoadingGroup(null);
    }
  }

  return (
    <main className="contacts-screen">
      <header className="contacts-header">
        <h2>Contatos por grupo</h2>
        <p className="muted">Clique num grupo para abrir os contatos salvos dele.</p>
      </header>

      {error && <p className="error">{error}</p>}
      {actionMsg && <p className="muted small">{actionMsg}</p>}

      {loading ? (
        <p className="muted">Carregando...</p>
      ) : groups.length === 0 ? (
        <p className="muted">Nenhum grupo com contatos ainda. Importe um grupo na aba Grupos.</p>
      ) : (
        <div className="group-containers">
          {groups.map((g) => {
            const open = expanded === g.groupTag;
            const list = contactsByGroup[g.groupTag];
            return (
              <div key={g.groupTag} className={`group-container${open ? " open" : ""}`}>
                <button type="button" className="group-container-head" onClick={() => void toggle(g.groupTag)}>
                  <span className="group-container-name">{g.groupTag}</span>
                  <span className="muted">
                    {g.count} contato(s) <span className="chevron">{open ? "▲" : "▼"}</span>
                  </span>
                </button>
                {open && (
                  <div className="group-container-body">
                    {loadingGroup === g.groupTag && !list ? (
                      <p className="muted small">Carregando...</p>
                    ) : list && list.length > 0 ? (
                      <table className="contacts-table">
                        <thead>
                          <tr>
                            <th>Nome</th>
                            <th>Telefone</th>
                            <th>Status</th>
                            <th>Ações</th>
                          </tr>
                        </thead>
                        <tbody>
                          {list.map((c) => (
                            <tr key={c.id} className={c.optOutAt ? "opted-out" : undefined}>
                              <td>{c.name || <span className="muted">—</span>}</td>
                              <td className="mono">{c.phoneE164}</td>
                              <td>
                                <StatusBadge contact={c} />
                              </td>
                              <td>
                                {c.optOutAt ? (
                                  <button
                                    type="button"
                                    className="reactivate-btn"
                                    title="Religar: volta a receber mensagens"
                                    onClick={() =>
                                      setConfirmTarget({
                                        id: c.id,
                                        tag: g.groupTag,
                                        label: c.name || c.phoneE164,
                                      })
                                    }
                                  >
                                    Reativar
                                  </button>
                                ) : c.stage === "Qualified" ? (
                                  <button
                                    type="button"
                                    className="reactivate-btn"
                                    disabled={busy}
                                    title='Tira o status "Respondeu" deste contato'
                                    onClick={() =>
                                      setPending({
                                        kind: "revert",
                                        id: c.id,
                                        tag: g.groupTag,
                                        label: c.name || c.phoneE164,
                                      })
                                    }
                                  >
                                    Reverter
                                  </button>
                                ) : (
                                  <span className="muted">—</span>
                                )}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    ) : (
                      <p className="muted small">Sem contatos neste grupo.</p>
                    )}
                    {/* Excluir só aparece com o grupo aberto — camada extra anti-miss-click. */}
                    <div className="group-delete-row">
                      <button
                        type="button"
                        className="group-delete-link"
                        disabled={busy}
                        title="Descarta os contatos deste grupo: somem das listas, do disparo e do chat, mas continuam no banco (opt-out preservado)."
                        onClick={() => setPending({ kind: "delete", tag: g.groupTag })}
                      >
                        Descartar contatos deste grupo
                      </button>
                    </div>
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}

      {confirmTarget && (
        <ConfirmDialog
          title="Reativar contato?"
          message={
            <>
              <strong>{confirmTarget.label}</strong> pediu para sair (opt-out). Ao reativar, ele volta
              para a base e <strong>poderá receber mensagens de disparo novamente</strong>.
              <br />
              <br />
              Tem certeza de que deseja fazer isso?
            </>
          }
          confirmLabel="Sim, reativar"
          cancelLabel="Cancelar"
          danger
          onConfirm={() => void reactivate(confirmTarget.id, confirmTarget.tag)}
          onCancel={() => setConfirmTarget(null)}
        />
      )}

      {pending && (
        <ConfirmDialog
          title={pending.kind === "revert" ? 'Reverter "Respondeu"?' : "Descartar contatos do grupo?"}
          message={
            pending.kind === "revert" ? (
              <>
                Tira o status <strong>"Respondeu"</strong> de <strong>{pending.label}</strong>. <strong>Não apaga
                nada</strong> e é reversível: se a pessoa responder de novo, volta para "Respondeu".
              </>
            ) : (
              <>
                Os contatos de <strong>“{pending.tag}”</strong> somem das listas, do disparo e do chat.
                <br />
                <br />
                <strong>Não são apagados do banco</strong> (o opt-out de quem saiu é preservado) e o{" "}
                <strong>WhatsApp do celular não é afetado</strong>.
              </>
            )
          }
          confirmLabel={pending.kind === "revert" ? "Sim, reverter" : "Sim, descartar"}
          cancelLabel="Cancelar"
          danger={pending.kind === "delete"}
          onConfirm={() => void confirmPending()}
          onCancel={() => setPending(null)}
        />
      )}
    </main>
  );
}
