import { useEffect, useState } from "react";
import { api } from "../api/client";
import { type Contact, type ContactGroupTag } from "../api/types";
import { AddContactsModal } from "./AddContactsModal";
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
    | { kind: "resend"; id: string; tag: string; label: string }
    | { kind: "discardOne"; id: string; tag: string; label: string }
    | { kind: "delete"; tag: string }
    | { kind: "purge"; tag: string }
    | { kind: "hardDelete"; tag: string }
    | null
  >(null);
  const [busy, setBusy] = useState(false);
  const [actionMsg, setActionMsg] = useState<string | null>(null);
  const [showAdd, setShowAdd] = useState(false);

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

  // Rebusca os contatos de um grupo e atualiza o cache local (após uma ação que muda um contato).
  async function refreshGroupContacts(tag: string) {
    const list = await api.listContacts({ groupTag: tag });
    setContactsByGroup((prev) => ({ ...prev, [tag]: list }));
  }

  async function reactivate(id: string, tag: string) {
    setConfirmTarget(null);
    try {
      await api.reactivateContact(id);
      await refreshGroupContacts(tag);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  // Após esvaziar um grupo (descarte/purge/hard delete): fecha o acordeão, limpa o cache local e
  // recarrega a lista de grupos (o grupo vazio some sozinho).
  async function forgetEmptiedGroup(tag: string) {
    setExpanded((e) => (e === tag ? null : e));
    setContactsByGroup((prev) => {
      const next = { ...prev };
      delete next[tag];
      return next;
    });
    setGroups(await api.listContactGroupTags());
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
        await refreshGroupContacts(pending.tag);
      } else if (pending.kind === "resend") {
        await api.resendContact(pending.id);
        setActionMsg(`"${pending.label}" liberado para um novo disparo.`);
        await refreshGroupContacts(pending.tag);
      } else if (pending.kind === "discardOne") {
        await api.discardContact(pending.id);
        setActionMsg(`"${pending.label}" excluído.`);
        // Some da lista; se o grupo esvaziou, some da lista de grupos. Atualiza os dois.
        await refreshGroupContacts(pending.tag);
        setGroups(await api.listContactGroupTags());
      } else if (pending.kind === "delete") {
        const r = await api.deleteGroupContacts(pending.tag);
        setActionMsg(`${r.deleted} contato(s) do grupo "${pending.tag}" excluído(s).`);
        await forgetEmptiedGroup(pending.tag);
      } else if (pending.kind === "purge") {
        const r = await api.purgeGroupContacts(pending.tag);
        setActionMsg(
          `${r.purged} contato(s) apagado(s) de vez do grupo "${pending.tag}"` +
            (r.keptOptedOut > 0 ? `; ${r.keptOptedOut} com opt-out preservado(s).` : "."),
        );
        await forgetEmptiedGroup(pending.tag);
      } else {
        const r = await api.hardDeleteGroupContacts(pending.tag);
        setActionMsg(`${r.deleted} contato(s) apagado(s) permanentemente do grupo "${pending.tag}".`);
        await forgetEmptiedGroup(pending.tag);
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

  // Config do modal de confirmação por tipo de ação pendente. Isola o copy do JSX e mantém o
  // discriminated-union type-safe: cada case estreita `p` (label só existe nas ações de contato,
  // tag nas de grupo). Retorna as props que variam do ConfirmDialog.
  function pendingDialog(p: NonNullable<typeof pending>) {
    switch (p.kind) {
      case "revert":
        return {
          title: 'Reverter "Respondeu"?',
          confirmLabel: "Sim, reverter",
          danger: false,
          message: (
            <>
              Tira o status <strong>"Respondeu"</strong> de <strong>{p.label}</strong>.{" "}
              <strong>Não apaga nada</strong> e é reversível: se a pessoa responder de novo, volta para
              "Respondeu".
            </>
          ),
        };
      case "resend":
        return {
          title: "Reenviar para este contato?",
          confirmLabel: "Sim, liberar",
          danger: false,
          message: (
            <>
              Libera <strong>{p.label}</strong> para um <strong>novo disparo</strong> (zera a marca de "já
              enviado" só dele). No próximo envio ele volta a receber. <strong>Não apaga nada.</strong>
            </>
          ),
        };
      case "discardOne":
        return {
          title: "Excluir este contato?",
          confirmLabel: "Sim, excluir",
          danger: true,
          message: (
            <>
              <strong>{p.label}</strong> some das listas, do disparo, do chat e do{" "}
              <strong>resultado dos envios</strong>.
              <br />
              <br />
              <strong>Não é apagado do banco</strong> (o opt-out é preservado) e o{" "}
              <strong>WhatsApp do celular não é afetado</strong>. É reversível.
            </>
          ),
        };
      case "delete":
        return {
          title: "Descartar contatos do grupo?",
          confirmLabel: "Sim, descartar",
          danger: true,
          message: (
            <>
              Os contatos de <strong>“{p.tag}”</strong> somem das listas, do disparo, do chat e do{" "}
              <strong>resultado dos envios</strong>.
              <br />
              <br />
              <strong>Não são apagados do banco</strong> (o opt-out de quem saiu é preservado) e o{" "}
              <strong>WhatsApp do celular não é afetado</strong>. É reversível.
            </>
          ),
        };
      case "purge":
        return {
          title: "Apagar permanentemente (preserva quem saiu)?",
          confirmLabel: "Sim, apagar",
          danger: true,
          message: (
            <>
              Os contatos de <strong>“{p.tag}”</strong> <strong>sem opt-out</strong> são{" "}
              <strong>APAGADOS DE VEZ do banco</strong> — junto com suas conversas, mensagens e jobs de
              disparo. <strong>Isto é irreversível.</strong>
              <br />
              <br />
              Quem pediu <strong>“SAIR”</strong> é preservado (fica só descartado) para continuar suprimido
              no anti-ban. O WhatsApp do celular não é afetado.
            </>
          ),
        };
      case "hardDelete":
        return {
          title: "Apagar TUDO permanentemente?",
          confirmLabel: "Sim, apagar tudo",
          danger: true,
          message: (
            <>
              <strong>TODOS</strong> os contatos de <strong>“{p.tag}”</strong> — inclusive{" "}
              <strong>quem deu “SAIR”</strong> — são <strong>APAGADOS DE VEZ do banco</strong>, com conversas,
              mensagens e jobs. <strong>Irreversível e sem backup.</strong>
              <br />
              <br />
              ⚠️ <strong>Anti-ban:</strong> quem pediu para sair perde a marca de opt-out e pode ser
              reimportado e disparado de novo no próximo sync.
            </>
          ),
        };
    }
  }

  return (
    <main className="contacts-screen">
      <header className="contacts-header">
        <div className="contacts-header-top">
          <h2>Contatos por grupo</h2>
          <button type="button" onClick={() => setShowAdd(true)}>
            Adicionar números
          </button>
        </div>
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
                            <tr
                              key={c.id}
                              className={
                                c.optOutAt
                                  ? "opted-out"
                                  : !c.fromCurrentChip
                                    ? "other-chip"
                                    : undefined
                              }
                              title={
                                !c.fromCurrentChip
                                  ? "Contato de OUTRO chip — o chip conectado agora não está no grupo dele, então o disparo NÃO envia (evita 463). Re-importe o grupo com este chip para habilitar."
                                  : undefined
                              }
                            >
                              <td>{c.name}</td>
                              <td className="mono">{c.phoneE164}</td>
                              <td>
                                <StatusBadge contact={c} />
                                {!c.fromCurrentChip && <span className="other-chip-badge">outro chip</span>}
                              </td>
                              <td>
                                <div className="contact-actions">
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
                                  ) : null}
                                  {/* Reenviar só faz sentido pra quem já recebeu (tem LastSentAt) e não saiu. */}
                                  {!c.optOutAt && c.lastSentAt && (
                                    <button
                                      type="button"
                                      className="reactivate-btn"
                                      disabled={busy}
                                      title="Libera este contato pra um novo disparo (zera o 'já enviado' só dele)"
                                      onClick={() =>
                                        setPending({
                                          kind: "resend",
                                          id: c.id,
                                          tag: g.groupTag,
                                          label: c.name || c.phoneE164,
                                        })
                                      }
                                    >
                                      Reenviar
                                    </button>
                                  )}
                                  <button
                                    type="button"
                                    className="contact-delete-btn"
                                    disabled={busy}
                                    title="Exclui (descarta) este contato: some das listas, do disparo, do chat e do resultado dos envios. Continua no banco (opt-out preservado)."
                                    onClick={() =>
                                      setPending({
                                        kind: "discardOne",
                                        id: c.id,
                                        tag: g.groupTag,
                                        label: c.name || c.phoneE164,
                                      })
                                    }
                                  >
                                    Excluir
                                  </button>
                                </div>
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    ) : (
                      <p className="muted small">Sem contatos neste grupo.</p>
                    )}
                    {/* Ações de grupo só aparecem com o grupo aberto — camada extra anti-miss-click. */}
                    <div className="group-delete-row">
                      <button
                        type="button"
                        className="group-delete-link"
                        disabled={busy}
                        title="Descarta os contatos deste grupo: somem das listas, do disparo, do chat e do resultado dos envios, mas continuam no banco (opt-out preservado). Reversível."
                        onClick={() => setPending({ kind: "delete", tag: g.groupTag })}
                      >
                        Descartar contatos deste grupo
                      </button>
                      <button
                        type="button"
                        className="group-delete-link danger"
                        disabled={busy}
                        title="APAGA DE VEZ do banco os contatos sem opt-out (com conversas, mensagens e jobs). Quem deu SAIR é preservado (descartado). Irreversível."
                        onClick={() => setPending({ kind: "purge", tag: g.groupTag })}
                      >
                        Apagar permanentemente (preserva quem saiu)
                      </button>
                      <button
                        type="button"
                        className="group-delete-link danger"
                        disabled={busy}
                        title="APAGA DE VEZ TODOS os contatos do grupo, INCLUSIVE quem deu SAIR (perde o opt-out — risco anti-ban). Irreversível e sem backup."
                        onClick={() => setPending({ kind: "hardDelete", tag: g.groupTag })}
                      >
                        Apagar tudo permanentemente
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
          {...pendingDialog(pending)}
          cancelLabel="Cancelar"
          onConfirm={() => void confirmPending()}
          onCancel={() => setPending(null)}
        />
      )}

      {showAdd && (
        <AddContactsModal
          onClose={() => setShowAdd(false)}
          onSaved={async (r) => {
            if (r.added === 0 && r.duplicated === 0) return;
            try {
              // Novos contatos podem ter criado/realimentado o grupo "Avulsos" → recarrega a lista
              // de grupos e, se algum grupo afetado estiver aberto, rebusca seus contatos.
              setGroups(await api.listContactGroupTags());
              if (expanded) {
                const list = await api.listContacts({ groupTag: expanded });
                setContactsByGroup((prev) => ({ ...prev, [expanded]: list }));
              }
            } catch (ex) {
              // O cadastro já foi salvo; só o refresh da lista falhou. Mostra o erro sem derrubar nada.
              setError(ex instanceof Error ? ex.message : String(ex));
            }
          }}
        />
      )}
    </main>
  );
}
