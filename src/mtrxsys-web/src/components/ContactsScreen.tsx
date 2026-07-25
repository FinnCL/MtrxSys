import { useEffect, useState } from "react";
import { api, type ValidationStatus } from "../api/client";
import { type Contact, type ContactGroupTag } from "../api/types";
import { AddContactsModal } from "./AddContactsModal";
import { ConfirmDialog } from "./ConfirmDialog";
import { GroupImportPanel } from "./GroupImportPanel";
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
    | null
  >(null);
  const [busy, setBusy] = useState(false);
  const [actionMsg, setActionMsg] = useState<string | null>(null);
  const [showAdd, setShowAdd] = useState(false);
  // "Importar de grupos" aqui na aba Contatos (mesmo painel da aba Grupos): re-importar retag os
  // contatos pro chip conectado AGORA (habilita o disparo por ele). Colapsado por default.
  const [showGroupImport, setShowGroupImport] = useState(false);
  // Círculo de Aquecimento: telefone (E.164) -> id do membro. Marca quais contatos SEUS re-enviam na
  // fase híbrida (dia 4+). Persistente — carregado uma vez e alterado pelos checkboxes.
  const [circle, setCircle] = useState<Map<string, string>>(new Map());
  // "Validar lista" (pré-voo anti-463): progresso da checagem de existência dos Leads no WhatsApp.
  const [validation, setValidation] = useState<ValidationStatus | null>(null);

  const startValidation = async () => {
    setActionMsg(null);
    try {
      const r = await api.contactsValidateStart();
      setValidation(r.status); // running=true dispara o poll abaixo
      if (!r.started) setActionMsg("Validação já está em andamento.");
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  };

  // Enquanto a validação roda, faz poll do progresso a cada 4s. Ao terminar, recarrega as listas (os
  // descartados saíram da fila).
  useEffect(() => {
    if (!validation?.running) return;
    let alive = true;
    const t = setInterval(async () => {
      try {
        const s = await api.contactsValidateStatus();
        if (!alive) return;
        setValidation(s);
        if (!s.running) {
          try { setGroups(await api.listContactGroupTags()); } catch { /* ignore */ }
        }
      } catch { /* ignore */ }
    }, 4000);
    return () => { alive = false; clearInterval(t); };
  }, [validation?.running]);

  useEffect(() => {
    void (async () => {
      setLoading(true);
      try {
        setGroups(await api.listContactGroupTags());
        setCircle(new Map((await api.listWarmupCircle()).map((m) => [m.phone, m.id])));
        setError(null);
      } catch (ex) {
        setError(ex instanceof Error ? ex.message : String(ex));
      } finally {
        setLoading(false);
      }
    })();
  }, []);

  // Liga/desliga o contato no Círculo de Aquecimento (pool re-enviável da fase híbrida). Persistente:
  // marca uma vez e fica. Chaveado pelo E.164 do contato (bate com o telefone normalizado do membro).
  async function toggleCircle(c: Contact) {
    const existingId = circle.get(c.phoneE164);
    try {
      if (existingId) {
        await api.removeFromWarmupCircle(existingId);
        setCircle((prev) => {
          const m = new Map(prev);
          m.delete(c.phoneE164);
          return m;
        });
      } else {
        const member = await api.addToWarmupCircle({ phone: c.phoneE164, name: c.name });
        setCircle((prev) => new Map(prev).set(c.phoneE164, member.id));
      }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

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

  // Após descartar os contatos de um grupo: fecha o acordeão, limpa o cache local e recarrega a
  // lista de grupos (o grupo vazio some sozinho).
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
        setActionMsg(`${r.deleted} contato(s) da lista "${pending.tag}" excluído(s).`);
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
          title: "Descartar contatos da lista?",
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
    }
  }

  return (
    <main className="contacts-screen">
      <header className="contacts-header">
        <div className="contacts-header-top">
          {/* "Lista", e não "grupo": isto é etiqueta do CRM (groupTag) pra organizar contatos —
              não tem nada a ver com o grupo do WhatsApp da aba Grupos, que tem conversa e membros.
              Os dois já se chamavam "grupo" e a confusão era garantida. O nome interno (groupTag)
              fica como está: renomeá-lo em API/banco é risco sem ganho pra quem usa. */}
          <h2>Contatos por lista</h2>
          <button type="button" onClick={() => setShowAdd(true)}>
            Adicionar números
          </button>
          {/* Validar lista (pré-voo anti-463): confirma quais têm WhatsApp e descarta os inexistentes
              ANTES do disparo. Paced (8-20s/número) — some do grupo raspado o que não tem conta. */}
          <button type="button" onClick={() => void startValidation()} disabled={validation?.running}>
            {validation?.running ? "Validando…" : "Validar números"}
          </button>
          {/* Importar de grupos aqui mesmo (mesmo painel da aba Grupos). Re-importar depois de trocar o
              chip "move" os contatos pro chip novo (ImportedByPhone) e habilita o disparo por ele. */}
          <button type="button" onClick={() => setShowGroupImport((v) => !v)}>
            {showGroupImport ? "Fechar importar" : "Importar de grupos"}
          </button>
        </div>
        {validation && (
          <p className="muted small">
            Validação: {validation.done}/{validation.total} · <strong>{validation.valid}</strong> com
            WhatsApp · <strong>{validation.invalid}</strong> descartados (sem conta)
            {validation.uncertain > 0 ? ` · ${validation.uncertain} indeterminados` : ""}
            {validation.running ? " · em andamento…" : " · concluído"}
          </p>
        )}
        <p className="muted">
          Clique numa lista para abrir os contatos salvos dela. Lista é só uma etiqueta pra organizar
          seus contatos aqui, não é o grupo do WhatsApp.
        </p>
        <p className="muted small">
          <strong>Aquecer:</strong> na fase híbrida (dia 4+), quem <strong>respondeu</strong> vai pra
          frente da fila e é <strong>renovado</strong> (re-disparado). A marca é automática ao responder.
          Contato frio nunca renova (envia 1× só). Desmarque para parar de renovar.
        </p>
      </header>

      {showGroupImport && (
        <section className="contacts-group-import">
          <p className="muted small">
            Importe os participantes de um grupo como contatos. <strong>Re-importar</strong> depois de
            trocar o chip "move" os contatos pro chip conectado agora — sem isso o disparo os pula (são
            de "outro chip", anti-463).
          </p>
          <GroupImportPanel
            onImported={() => {
              // Recarrega as listas: a lista do grupo importado passa a aparecer aqui.
              void (async () => {
                try {
                  setGroups(await api.listContactGroupTags());
                } catch {
                  /* recarga é um extra; o import em si já foi */
                }
              })();
            }}
          />
        </section>
      )}

      {error && <p className="error">{error}</p>}
      {actionMsg && <p className="muted small">{actionMsg}</p>}

      {loading ? (
        <p className="muted">Carregando...</p>
      ) : groups.length === 0 ? (
        // "grupo" no fim da frase é o do WhatsApp de propósito — é de lá que vem a importação.
        <p className="muted">Nenhuma lista com contatos ainda. Importe um grupo na aba Grupos.</p>
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
                            <th title="Quem respondeu vai pra frente da fila e é renovado (re-disparado) na fase híbrida (dia 4+). Auto-marcado ao responder; frio não pode ser marcado (1× só).">Aquecer</th>
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
                                <input
                                  type="checkbox"
                                  checked={circle.has(c.phoneE164)}
                                  onChange={() => void toggleCircle(c)}
                                  disabled={!!c.optOutAt || c.stage === "Lead" || c.stage === "Lost"}
                                  title={
                                    c.stage === "Lead" || c.stage === "Lost"
                                      ? "Só quem já respondeu pode ser aquecido (o Aquecer renova o disparo; frio não renova)."
                                      : "Aquecer: prioriza e renova este contato na fase híbrida (dia 4+). Auto-marcado quando responde; desmarque pra parar de renovar."
                                  }
                                />
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
                      <p className="muted small">Sem contatos nesta lista.</p>
                    )}
                    {/* Ações de grupo só aparecem com o grupo aberto — camada extra anti-miss-click. */}
                    <div className="group-delete-row">
                      <button
                        type="button"
                        className="group-delete-link"
                        disabled={busy}
                        title="Descarta os contatos desta lista: somem do disparo, do chat e do resultado dos envios, mas continuam no banco (opt-out preservado). Reversível."
                        onClick={() => setPending({ kind: "delete", tag: g.groupTag })}
                      >
                        Descartar contatos desta lista
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
