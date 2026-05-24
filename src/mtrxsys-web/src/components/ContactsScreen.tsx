import { useEffect, useState } from "react";
import { api } from "../api/client";
import { STAGE_LABELS, type Contact, type ContactGroupTag } from "../api/types";

export function ContactsScreen() {
  const [groups, setGroups] = useState<ContactGroupTag[]>([]);
  const [expanded, setExpanded] = useState<string | null>(null);
  const [contactsByGroup, setContactsByGroup] = useState<Record<string, Contact[]>>({});
  const [loadingGroup, setLoadingGroup] = useState<string | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

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
    try {
      await api.reactivateContact(id);
      const list = await api.listContacts({ groupTag: tag });
      setContactsByGroup((prev) => ({ ...prev, [tag]: list }));
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
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
                                <span className={`stage-badge stage-${c.stage.toLowerCase()}`}>
                                  {STAGE_LABELS[c.stage]}
                                </span>
                              </td>
                              <td>
                                {c.optOutAt ? (
                                  <button
                                    type="button"
                                    className="reactivate-btn"
                                    title="Religar: volta a receber mensagens"
                                    onClick={() => void reactivate(c.id, g.groupTag)}
                                  >
                                    Reativar
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
                  </div>
                )}
              </div>
            );
          })}
        </div>
      )}
    </main>
  );
}
