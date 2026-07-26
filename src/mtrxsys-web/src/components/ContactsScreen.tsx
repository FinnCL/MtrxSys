import { useEffect, useState } from "react";
import { api, type ValidationStatus } from "../api/client";
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
    | null
  >(null);
  const [busy, setBusy] = useState(false);
  const [actionMsg, setActionMsg] = useState<string | null>(null);
  const [showAdd, setShowAdd] = useState(false);
  // "Migrar contatos para este chip": ação destrutiva do ponto de vista anti-ban (afirma um vínculo que
  // pode não existir), por isso passa por confirmação com o risco explicado.
  const [confirmReassign, setConfirmReassign] = useState(false);
  const [reassigning, setReassigning] = useState(false);
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

  // Regrava o dono dos contatos para o chip conectado. Recarrega as listas no fim: quem estava cinza
  // ("outro chip") passa a aparecer habilitado, e ver isso acontecer é a confirmação de que funcionou.
  const reassignToCurrentChip = async () => {
    setConfirmReassign(false);
    setReassigning(true);
    setActionMsg(null);
    try {
      const r = await api.contactsReassignToCurrentChip();
      // Os dois números são INDEPENDENTES: numa segunda execução `moved` pode ser 0 (contatos já
      // migrados) e ainda assim haver envios pulados voltando. Aninhar o aviso dentro do `moved > 0`
      // escondia justamente esse caso — o usuário leria "nada mudou" enquanto a fila se enchia.
      const parteContatos = r.moved === 0
        ? `Nenhum contato precisou mudar: os ${r.total} já pertencem ao chip ${r.chip}.`
        : `${r.moved} de ${r.total} contatos passaram para o chip ${r.chip}.`;
      const parteFila = r.requeued > 0
        ? ` ${r.requeued} ${r.requeued === 1 ? "envio pulado voltou" : "envios pulados voltaram"} para a fila.`
        : "";
      setActionMsg(parteContatos + parteFila);
      setGroups(await api.listContactGroupTags());
      // Descarta o cache das listas já abertas: elas guardam o `fromCurrentChip` ANTIGO e continuariam
      // mostrando "outro chip" em cinza mesmo depois da migração ter dado certo.
      setContactsByGroup({});
      setExpanded(null);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setReassigning(false);
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
        setError(null);
      } catch (ex) {
        setError(ex instanceof Error ? ex.message : String(ex));
      } finally {
        setLoading(false);
      }
      // Adota o progresso de uma validação JÁ EM ANDAMENTO. O estado vive no servidor (singleton em
      // memória), mas quem sabia dele era só a aba que clicou: recarregar a página durante uma varredura
      // de horas apagava a barra de progresso, e clicar de novo só respondia "já está em andamento" —
      // sem número nenhum. O status é leitura em memória, sem banco, então custa nada no mount.
      //
      // SÓ quando `running`. Um resultado JÁ TERMINADO adotado no mount reapareceria como
      // "127/127 · concluído" em toda carga da página, sem data, por todo o tempo de vida da api — e a
      // pessoa leria "minha lista está conferida" mesmo tendo importado 300 contatos novos depois. O
      // status não carrega quando aconteceu, então mostrá-lo fora do "em andamento" afirma mais do que
      // se sabe. Em andamento não tem essa ambiguidade: está acontecendo agora.
      try {
        const s = await api.contactsValidateStatus();
        if (s.running) {
          setValidation(s); // religa o poll do efeito acima
        }
      } catch {
        /* progresso é acessório: a tela funciona sem ele */
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

  // A validação já rodou nesta api? `total`/`done` zerados são iguais em "nunca rodou" e em "rodou e não
  // achou ninguém"; `message` é o que separa os dois (o estado inicial do runner vem com ele nulo).
  // `!= null` cobre null E undefined DE PROPÓSITO: hoje a api serializa o nulo explicitamente, mas se
  // algum dia ligarem `DefaultIgnoreCondition.WhenWritingNull` o campo passa a vir AUSENTE — e um
  // `!== null` inverteria este teste em silêncio, fazendo a tela gritar "nada foi validado" a cada carga.
  const validationRan = !!validation && (validation.running || validation.message != null);
  // NÃO TERMINOU o que prometeu: parou com gente ainda por checar. O runner ABORTA a varredura quando as
  // primeiras checagens vêm todas indeterminadas (sessão WhatsApp fora) e devolve running=false com
  // done < total — e a UI antiga escrevia "· concluído" em cima disso. "Concluído" é lido como licença pra
  // disparar, então o operador dispararia pra uma lista NÃO conferida: o pré-voo anti-463 falhando em
  // silêncio, com cara de sucesso.
  //
  // `done < total` E SÓ ISSO. `total === 0` também parece suspeito, mas é o caso LEGÍTIMO de não haver
  // ninguém pendente pra validar — alarme vermelho ali seria grito de lobo, e aviso anti-ban que aparece
  // quando nada está errado é aviso que o operador aprende a ignorar (foi exatamente o vício do "marco
  // órfão"). Se `total` é 0 por erro de verdade, o desfecho do runner ("erro") já aparece na linha de
  // status — quieto, mas sem nunca fingir sucesso.
  const validationIncomplete =
    !!validation && !validation.running && validation.total > 0 && validation.done < validation.total;

  return (
    <main className="contacts-screen">
      <header className="contacts-header">
        {/* "Lista", e não "grupo": isto é etiqueta do CRM (groupTag) pra organizar contatos —
            não tem nada a ver com o grupo do WhatsApp da aba Grupos, que tem conversa e membros.
            Os dois já se chamavam "grupo" e a confusão era garantida. O nome interno (groupTag)
            fica como está: renomeá-lo em API/banco é risco sem ganho pra quem usa. */}
        <h2>Contatos por lista</h2>
        <p className="muted">
          Clique numa lista para abrir os contatos salvos dela. Lista é só uma etiqueta pra organizar
          seus contatos aqui, não é o grupo do WhatsApp.
        </p>

        {/* PASSO A PASSO em vez de uma fileira de botões. A fileira tratava como iguais coisas de
            peso muito diferente — "Validar números" é PRÉ-REQUISITO do disparo (disparar pra número
            inexistente é o que queima o chip) e ficava indistinguível de uma ação corretiva rara.
            A ordem numerada carrega essa informação sem exigir que o operador já a saiba. */}
        <ol className="contacts-steps">
          <li className="contacts-step">
            {/* aria-hidden: o número já é anunciado pelo <ol>. Sem isto o leitor de tela diz "1, 1". */}
            <span className="contacts-step-num" aria-hidden="true">1</span>
            <div className="contacts-step-body">
              <h3>Trazer os contatos</h3>
              {/* Sem `title` nos botões destes passos: o texto do passo JÁ diz o que o tooltip dizia, e
                  manter as duas cópias só garante que uma delas fique desatualizada. */}
              <p className="contacts-step-sub">
                Cole ou digite números aqui, escolhendo em qual lista salvar. Para trazer os participantes
                de um grupo do WhatsApp, use a aba <strong>Grupos</strong>.
              </p>
              <button type="button" onClick={() => setShowAdd(true)}>
                Adicionar números
              </button>
            </div>
          </li>

          {/* Validar lista (pré-voo anti-463): confirma quais têm WhatsApp e descarta os inexistentes
              ANTES do disparo. Paced (8-20s/número) — some do grupo raspado o que não tem conta.
              É o passo DESTACADO: sem ele o disparo trabalha contra o próprio chip. */}
          <li className="contacts-step contacts-step-key">
            <span className="contacts-step-num" aria-hidden="true">2</span>
            <div className="contacts-step-body">
              <h3>
                Validar os números <span className="contacts-step-flag">essencial</span>
              </h3>
              {/* Diz SOBRE O QUE ele roda. Sem isso a leitura natural é "valida o que eu acabei de
                  colar", e quem tem a base vazia clica esperando algo e recebe 0/0 sem entender por quê.
                  O runner varre todo contato em estágio Lead não-optado, de QUALQUER origem (passo 1 ou
                  importação na aba Grupos) — quem já respondeu não é re-checado, porque conversar já
                  prova que a conta existe. */}
              <p className="contacts-step-sub">
                Roda sobre os contatos <strong>já cadastrados</strong> (do passo 1 ou importados em
                Grupos) que ainda não responderam — não é preciso importar de grupo pra usar. Confere um
                a um quem tem conta no WhatsApp e descarta os que não têm.{" "}
                <strong>Disparar para número inexistente é o que queima o chip.</strong> Leva alguns
                minutos.
              </p>
              <button
                type="button"
                className="contacts-step-cta"
                onClick={() => void startValidation()}
                disabled={validation?.running}
              >
                {validation?.running ? "Validando…" : "Validar números"}
              </button>
              {validation && validationRan && (
                <div className="contacts-step-progress">
                  <p className="muted small contacts-step-status">
                    Validação: {validation.done}/{validation.total} · <strong>{validation.valid}</strong>{" "}
                    com WhatsApp · <strong>{validation.invalid}</strong> descartados (sem conta)
                    {validation.uncertain > 0 ? ` · ${validation.uncertain} indeterminados` : ""}
                    {/* O desfecho vem do RUNNER (`message`), não de um rótulo fixo daqui: é ele que sabe
                        se concluiu, abortou por sessão fora, deu erro ou foi interrompido no shutdown. */}
                    {validation.running ? " · em andamento…" : ` · ${validation.message ?? "concluído"}`}
                  </p>
                  {validationIncomplete && (
                    <p className="contacts-step-warn">
                      <strong>Parou antes do fim.</strong> Faltaram{" "}
                      {validation.total - validation.done} número(s) e a lista{" "}
                      <strong>não está conferida</strong> — resolva o motivo acima e rode de novo antes de
                      disparar.
                    </p>
                  )}
                </div>
              )}
            </div>
          </li>

          {/* Migrar contatos: resolve o caso que a re-importação NÃO resolve (contato manual nunca veio
              de grupo). Afrouxa a trava anti-463, por isso passa por confirmação com o risco escrito.
              Numerado como os outros, mas rotulado "só se precisar": um passo 3 sem essa marca convida
              a fazer sempre, e "sempre" aqui significa desligar a proteção por hábito. */}
          <li className="contacts-step contacts-step-optional">
            <span className="contacts-step-num" aria-hidden="true">3</span>
            <div className="contacts-step-body">
              <h3>
                Migrar para este chip{" "}
                <span className="contacts-step-flag is-muted">só se precisar</span>
              </h3>
              <p className="contacts-step-sub">
                Contatos marcados <strong>“outro chip”</strong> não recebem. O caminho normal é
                re-importar o grupo na aba <strong>Grupos</strong>; use isto quando não houver como
                (contatos adicionados à mão, por exemplo).
              </p>
              <button type="button" onClick={() => setConfirmReassign(true)} disabled={reassigning}>
                {reassigning ? "Migrando…" : "Migrar contatos para este chip"}
              </button>
            </div>
          </li>
        </ol>
      </header>

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

      {confirmReassign && (
        <ConfirmDialog
          title="Migrar os contatos para este chip?"
          message={
            <>
              Todos os contatos passam a pertencer ao <strong>chip conectado agora</strong> e o disparo
              volta a enviar para eles.
              <br /><br />
              <strong>Isto afrouxa uma proteção.</strong> O sistema só envia para contatos que vieram de
              um grupo do próprio chip, porque enviar para quem não tem relação com ele dá erro 463, que
              é gatilho de banimento. Re-importar o grupo prova esse vínculo; migrar na mão apenas o
              afirma.
              <br /><br />
              Use quando <strong>não houver como re-importar</strong> (contatos adicionados à mão, por
              exemplo) e você souber que este chip tem relação com essas pessoas.
            </>
          }
          confirmLabel="Sim, migrar"
          cancelLabel="Cancelar"
          danger
          onConfirm={() => void reassignToCurrentChip()}
          onCancel={() => setConfirmReassign(false)}
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
