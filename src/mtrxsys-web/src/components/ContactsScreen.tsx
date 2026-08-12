import { useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import { contactStatusBadge, type Contact, type ContactGroupTag } from "../api/types";
import { copyText } from "../utils/clipboard";
import { downloadContactsXlsx } from "../utils/exportContacts";
import { ConfirmDialog } from "./ConfirmDialog";
import { GoogleImportPanel } from "./GoogleImportPanel";
import { StatusBadge } from "./StatusBadge";

// O "+" sai do número BRASILEIRO e SÓ dele. Nos dois destinos prováveis da cópia (busca do WhatsApp
// e console do aparelho) o que falta é completado com o código do país do aparelho, que é 55 — então
// pro +55 o sinal é redundante, e o driver automático já digita sem ele (`Where(char.IsDigit)` em
// WhatsAppUiDriver.cs:41). Tirar de TODOS deixaria a lista bonita e uniforme e quebraria os de fora
// calado: sem o "+", "+2349054438019" (Nigéria) é lido como um número do Brasil e vira mensagem pra
// outra pessoa, ou pra ninguém. Uniformidade não vale um número entregue errado.
const semMais = (e164: string) => (e164.startsWith("+55") ? e164.slice(1) : e164);

// Um telefone por linha: é o formato que cola direto em campo de busca, em bloco de notas e no
// console do aparelho, sem o operador ter que limpar nada.
//
// 🔴 SEM quem pediu para sair. Este é o único formato que perde o status pelo caminho, e o destino
// provável dele é o console do aparelho, que por escopo "não tem fila, curva de aquecimento, opt-out,
// dedup entre execuções nem auditoria no banco" (PhoneConsoleCommand). Ou seja: colado lá, o número
// vira mensagem sem ninguém mais conferir se aquela pessoa mandou SAIR. A tela é a última barreira,
// então ela não entrega esses números nesse formato. Não é cortar informação calado: a contagem dos
// excluídos vai no aviso, e os outros dois formatos (nome e telefone, planilha) levam TODOS com o
// status ao lado, que é o que permite decidir caso a caso.
const telefonesDaLista = (list: Contact[]) =>
  list.filter((c) => !c.optOutAt).map((c) => semMais(c.phoneE164)).join("\n");

// TAB e quebra de linha SÃO os separadores do formato colado. Nome de contato vem do WhatsApp ou da
// agenda Google, onde cabe qualquer caractere: um TAB no nome empurra as colunas seguintes e a linha
// inteira cola errada, calada. Vira espaço antes de entrar no texto.
const semSeparadores = (texto: string) => texto.replace(/[\t\r\n]+/g, " ").trim();

// Nome, telefone e status separados por TAB. Serve pra colar em texto (chat, bloco de notas, ticket).
//
// Pra PLANILHA continua valendo o botão de baixar .xlsx ao lado, e tirar o "+" NÃO resolveu isso —
// só trocou o estrago. Com "+", o Excel e o Sheets liam "+5511..." como início de FÓRMULA. Sem
// "+", "5511965146354" é um número de 13 dígitos e a célula mostra 5,51197E+12. Os dois caminhos
// entregam telefone adulterado; o .xlsx grava texto de verdade e não passa por parser nenhum.
const tabelaDaLista = (list: Contact[]) =>
  [
    "Nome\tTelefone\tStatus",
    ...list.map(
      (c) =>
        `${semSeparadores(c.name ?? "")}\t${semMais(c.phoneE164)}\t${semSeparadores(contactStatusBadge(c).label)}`,
    ),
  ].join("\n");

// Aviso da cópia de telefones: quantos saíram no texto e o que a lista de números NÃO carrega
// consigo. A conta é dita por inteiro porque o número copiado perde o status que a tabela mostrava,
// e fora desta tela ninguém mais vai reconstruir essa informação.
//
// Opt-out é EXCLUÍDO (não pode receber, nunca) e contado no aviso. "Outro chip" é INCLUÍDO e avisado:
// aquele contato pode receber, só não por este chip; disparar por ele é risco de 463, e re-importar o
// grupo com o chip conectado resolve. Uma proibição e um risco não merecem o mesmo tratamento.
function avisoTelefones(list: Contact[], tag: string): { texto: string; alerta: boolean } {
  const saidas = list.filter((c) => c.optOutAt).length;
  const copiados = list.length - saidas;
  const vivos = list.filter((c) => !c.optOutAt);
  const outroChip = vivos.filter((c) => !c.fromCurrentChip).length;
  // Já receberam mensagem, por este ambiente (lastSentAt) ou por outro (sentElsewhere). O console do
  // aparelho não tem dedup entre execuções, então colar a lista inteira manda de novo pra essa gente,
  // e a mesma pessoa recebendo duas vezes é o padrão que faz o destinatário denunciar.
  const jaReceberam = vivos.filter((c) => c.lastSentAt || c.sentElsewhere).length;
  const partes = [`${copiados} telefone(s) copiado(s) da lista "${tag}".`];
  if (saidas > 0) {
    partes.push(`${saidas} ficaram fora da cópia por opt-out (pediram para sair).`);
  }
  if (jaReceberam > 0) {
    partes.push(`${jaReceberam} já receberam mensagem antes: colar tudo manda de novo pra eles.`);
  }
  if (outroChip > 0) {
    partes.push(`${outroChip} são de outro chip: disparar por este chip é risco de bloqueio (463).`);
  }
  return {
    texto: partes.join(" "),
    alerta: saidas > 0 || jaReceberam > 0 || outroChip > 0,
  };
}

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
  // Qual botão de copiar acabou de ser usado (id do contato, ou "lista:<tag>") e se deu certo, pra
  // trocar o rótulo dele por "Copiado!" ou "Falhou" por um instante. O retorno vai no PRÓPRIO botão
  // porque é o único ponto da tela que o operador com certeza está olhando quando clica.
  const [copiado, setCopiado] = useState<{ chave: string; ok: boolean } | null>(null);
  // Retorno da cópia. Fica JUNTO dos botões, e não no aviso do topo da tela: a lista aberta pode ter
  // centenas de linhas, o topo está fora da vista quando o operador clica lá embaixo, e um alerta que
  // não é visto não avisou nada.
  const [copiaMsg, setCopiaMsg] = useState<{ texto: string; alerta: boolean } | null>(null);
  const copiadoTimer = useRef<number | null>(null);

  // Limpa o timer se a tela sair do ar antes de ele disparar (evita setState em componente morto).
  useEffect(() => {
    return () => {
      if (copiadoTimer.current !== null) window.clearTimeout(copiadoTimer.current);
    };
  }, []);

  // Devolve o rótulo do botão ao normal depois de um tempo. Falha fica mais na tela que sucesso: é o
  // caso em que o operador precisa ler antes de o aviso sumir.
  function agendarLimpeza(ok: boolean) {
    if (copiadoTimer.current !== null) window.clearTimeout(copiadoTimer.current);
    copiadoTimer.current = window.setTimeout(() => setCopiado(null), ok ? 2000 : 5000);
  }

  // Copia e responde no botão. Falha (http sem contexto seguro, permissão negada) é dita em voz alta:
  // botão que não confirma nem reclama é pior que botão que não existe.
  async function copiar(
    chave: string,
    texto: string,
    aviso?: { texto: string; alerta: boolean },
    avisoVazio?: string,
  ) {
    // Texto vazio tratado ANTES de tentar copiar: a área de transferência recusaria, e a mensagem
    // genérica de falha mandaria o operador procurar problema de navegador quando o motivo é que não
    // sobrou ninguém pra copiar.
    if (!texto) {
      setCopiado({ chave, ok: false });
      setCopiaMsg({ texto: avisoVazio ?? "Nada a copiar nesta lista.", alerta: true });
      agendarLimpeza(false);
      return;
    }
    const ok = await copyText(texto);
    setCopiado({ chave, ok });
    setCopiaMsg(
      ok
        ? (aviso ?? null)
        : { texto: "Não consegui copiar. Selecione o texto na tela e use Ctrl+C.", alerta: true },
    );
    agendarLimpeza(ok);
  }

  // Rótulo do botão de copiar: "Copiado!" / "Falhou" só no botão que foi clicado.
  function rotuloCopia(chave: string, padrao: string): string {
    if (copiado?.chave !== chave) return padrao;
    return copiado.ok ? "Copiado!" : "Falhou";
  }


  // Extraída do useEffect pra poder ser rechamada: depois de importar da agenda Google, a lista de
  // listas muda (nasce/cresce a "Google") e a tela precisa refletir sem o operador dar F5.
  async function loadGroups() {
    setLoading(true);
    try {
      setGroups(await api.listContactGroupTags());
      setError(null);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setLoading(false);
    }
  }

  useEffect(() => {
    void loadGroups();
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
    // Zera o retorno da cópia: a contagem e o alerta valem pra UMA lista, e ficar pendurado ao abrir
    // outra faria o operador ler o aviso da lista errada.
    setCopiaMsg(null);
    setCopiado(null);
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
        {/* "Lista", e não "grupo": isto é etiqueta do CRM (groupTag) pra organizar contatos —
            não tem nada a ver com o grupo do WhatsApp da aba Grupos, que tem conversa e membros.
            Os dois já se chamavam "grupo" e a confusão era garantida. O nome interno (groupTag)
            fica como está: renomeá-lo em API/banco é risco sem ganho pra quem usa. */}
        <h2>Contatos por lista</h2>
        <p className="muted">
          Clique numa lista para abrir os contatos salvos dela. Lista é só uma etiqueta pra organizar
          seus contatos aqui, não é o grupo do WhatsApp.
        </p>

      </header>

      {/* Cadastrar fica na aba Contatos, não no Disparo: a tela de disparo já é a mais perigosa do
          sistema, e um botão que GRAVA contato ali convidaria a trazer gente nova segundos antes de
          mandar mensagem, sem tempo de revisar. */}
      <GoogleImportPanel onImported={() => void loadGroups()} />

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
                      <>
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
                                  {/* Copiar vem primeiro e SEMPRE aparece: é a única ação da linha
                                      que não muda nada no banco, e vale até pra contato de outro
                                      chip ou com opt-out (o operador quer o número, não disparar). */}
                                  <button
                                    type="button"
                                    className="contact-copy-btn"
                                    // O título mostra o que VAI PRO CLIPBOARD, não o que está na
                                    // célula ao lado: pro +55 os dois diferem agora, e um botão que
                                    // promete "+5511..." e entrega "5511..." ensina o operador a
                                    // desconfiar do resto da tela.
                                    title={`Copia o telefone ${semMais(c.phoneE164)} para a área de transferência`}
                                    onClick={() => void copiar(c.id, semMais(c.phoneE164))}
                                  >
                                    {rotuloCopia(c.id, "Copiar")}
                                  </button>
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
                      {/* Saída da lista inteira, logo abaixo da tabela. Três destinos, porque o
                          formato certo depende de onde vai colar: texto puro, texto em colunas, ou
                          planilha de verdade. Nenhum dos três grava nada. */}
                      <div className="contacts-copy-tools">
                        <button
                          type="button"
                          className="contact-copy-btn"
                          title="Copia os telefones desta lista, um por linha. Não inclui quem pediu para sair. Atenção: o número solto perde o status, e envio feito pelo console do aparelho não volta pra cá (o contato continua marcado como novo)."
                          onClick={() =>
                            void copiar(
                              `lista:${g.groupTag}`,
                              telefonesDaLista(list),
                              avisoTelefones(list, g.groupTag),
                              "Nada a copiar: todos os contatos desta lista pediram para sair (opt-out).",
                            )
                          }
                        >
                          {rotuloCopia(
                            `lista:${g.groupTag}`,
                            // A contagem no rótulo é a do que SAI na cópia (sem opt-out), não a da
                            // tabela: botão que promete 120 e entrega 117 é a mentira mais fácil de
                            // cometer aqui.
                            `Copiar telefones (${list.filter((c) => !c.optOutAt).length})`,
                          )}
                        </button>
                        <button
                          type="button"
                          className="contact-copy-btn"
                          title="Copia nome, telefone e status separados por TAB, pra colar em texto (chat, bloco de notas). Pra planilha, use o botão de baixar Excel."
                          onClick={() =>
                            void copiar(`tabela:${g.groupTag}`, tabelaDaLista(list), {
                              texto: `${list.length} linha(s) copiada(s) com nome, telefone e status.`,
                              alerta: false,
                            })
                          }
                        >
                          {rotuloCopia(`tabela:${g.groupTag}`, "Copiar nome e telefone")}
                        </button>
                        {/* Planilha é DOWNLOAD, não cópia: colado numa célula, o "+" do E.164 é lido
                            como início de fórmula pelo Excel e pelo Sheets, e o telefone chega
                            adulterado. O .xlsx grava texto e não passa por esse parser. */}
                        <button
                          type="button"
                          className="contact-copy-btn"
                          title="Baixa a lista em .xlsx com nome, telefone, grupo e status. Os telefones vão como texto, com o + preservado."
                          // A falha vai pro MESMO aviso que a cópia usa, logo abaixo destes botões:
                          // é o único ponto da tela que o operador está olhando quando clica aqui, e
                          // um download que não acontece precisa dizer isso em algum lugar.
                          onClick={() =>
                            void downloadContactsXlsx(list, g.groupTag).catch((ex: unknown) =>
                              setCopiaMsg({
                                texto: `Não consegui gerar a planilha: ${ex instanceof Error ? ex.message : String(ex)}`,
                                alerta: true,
                              }),
                            )
                          }
                        >
                          Baixar planilha (Excel)
                        </button>
                      </div>
                      {copiaMsg && (
                        <p className={copiaMsg.alerta ? "error small" : "muted small"}>{copiaMsg.texto}</p>
                      )}
                      </>
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

    </main>
  );
}
