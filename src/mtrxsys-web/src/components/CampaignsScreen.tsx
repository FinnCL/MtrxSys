import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type {
  ContactGroupTag,
  DispatchJobStatus,
  DispatchReportItem,
  DispatchStats,
  MessageTemplate,
  WarmupStatus,
} from "../api/types";
import { DISPATCH_STATUS_LABELS, downloadDispatchReportXlsx } from "../utils/exportContacts";
import { ConfirmDialog } from "./ConfirmDialog";

// Chave do localStorage onde fica a mensagem escolhida pra disparo. Persistir é o que
// faz a seleção sobreviver a F5/reabrir o navegador — sem isso, a tela voltava sempre
// pra primeira mensagem da lista.
const SELECTED_MESSAGE_STORAGE_KEY = "mtrx.campaigns.selectedMessageId";

// Texto inicial do campo: já traz a saudação em spintax e a linha de saída (SAIR),
// que deve estar sempre presente. O usuário escreve o miolo da mensagem no meio.
const DEFAULT_DRAFT =
  "{Oi|Olá|E aí}, {tudo bem|tudo certo}? {Tenho|Surgiu|Apareceu} {uma novidade|uma oferta|uma promoção} {que pode te interessar|que talvez te interesse|especial pra você}.\n\n{Entre no link e saiba mais|Dá uma olhada aqui|Confira os detalhes}: [cole seu link aqui]\n\nResponda SAIR para não receber mais mensagens.";

// Miniatura de uma mensagem com imagem. Busca o blob com auth (o endpoint exige
// Bearer, então <img src> direto daria 401) e usa um object URL, revogado ao desmontar.
function TemplateThumb({ id }: { id: string }) {
  const [src, setSrc] = useState<string | null>(null);
  useEffect(() => {
    let url: string | null = null;
    let cancelled = false;
    api
      .templateImageBlobUrl(id)
      .then((u) => {
        if (cancelled) {
          URL.revokeObjectURL(u);
          return;
        }
        url = u;
        setSrc(u);
      })
      .catch(() => {});
    return () => {
      cancelled = true;
      if (url) URL.revokeObjectURL(url);
    };
  }, [id]);
  return src ? <img className="message-thumb" src={src} alt="imagem da mensagem" /> : null;
}

const STAT_CHIPS: { key: DispatchJobStatus; label: string; cls: string }[] = [
  { key: "Pending", label: "Na fila", cls: "stat-pending" },
  { key: "Sent", label: "Enviadas", cls: "stat-sent" },
  { key: "Failed", label: "Falharam", cls: "stat-failed" },
  { key: "Skipped", label: "Puladas", cls: "stat-skipped" },
];

export function CampaignsScreen() {
  const [messages, setMessages] = useState<MessageTemplate[]>([]);
  const [groupTags, setGroupTags] = useState<ContactGroupTag[]>([]);
  // Seleção única: o disparo usa só UMA das mensagens salvas por vez. Persistida em
  // localStorage pra sobreviver a F5 e reabrir o navegador — sem isso, a tela perdia a
  // seleção do usuário a cada atualização e caía na primeira da lista. Se a salva sumiu
  // (mensagem apagada), o useEffect abaixo cai pra primeira disponível.
  const [selectedMessageId, setSelectedMessageId] = useState<string | null>(() => {
    try {
      return typeof window !== "undefined" ? window.localStorage.getItem(SELECTED_MESSAGE_STORAGE_KEY) : null;
    } catch {
      return null;
    }
  });
  const [stats, setStats] = useState<DispatchStats | null>(null);
  const [report, setReport] = useState<DispatchReportItem[]>([]);
  const [reportStatus, setReportStatus] = useState<"" | DispatchJobStatus>("");
  // Visão da tabela de resultados (só client-side): busca por telefone/nome e página atual.
  const [reportSearch, setReportSearch] = useState("");
  const [reportPage, setReportPage] = useState(1);
  const [draft, setDraft] = useState(DEFAULT_DRAFT);
  const [audience, setAudience] = useState<"all" | "responded">("all");
  const [group, setGroup] = useState("");
  const [adding, setAdding] = useState(false);
  const [dispatching, setDispatching] = useState(false);
  const [dispatchMsg, setDispatchMsg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [paused, setPaused] = useState(false);
  const [audienceCount, setAudienceCount] = useState<number | null>(null);
  const [warmup, setWarmup] = useState<WarmupStatus | null>(null);
  const [confirmClear, setConfirmClear] = useState(false);
  const [confirmRenew, setConfirmRenew] = useState(false);
  const [confirmStart, setConfirmStart] = useState(false);
  const [confirmRestartWarmup, setConfirmRestartWarmup] = useState(false);
  const [showDone, setShowDone] = useState(false);
  // Disjuntor aberto (pausa automática por falhas): horário UTC de retomada, ou null.
  const [circuitOpenUntil, setCircuitOpenUntil] = useState<string | null>(null);
  // Detecta o fim do envio (fila > 0 → 0 enquanto enviando). suppressDone evita o modal
  // quando o zero veio de uma ação manual (limpar fila / renovar lista).
  const prevPendingRef = useRef(0);
  const suppressDoneRef = useRef(false);
  // Modal de teto atingido: input do "+N" e flag pra não reabrir depois de cancelar hoje.
  const [capDismissed, setCapDismissed] = useState(false);
  const [extraInput, setExtraInput] = useState("");
  const [releasing, setReleasing] = useState(false);

  // Mantém o filtro atual acessível ao loadLive (callback estável, deps []) sem recriar o
  // intervalo a cada troca de filtro. Sincronizado via efeito pra não escrever ref na render.
  const reportStatusRef = useRef<"" | DispatchJobStatus>("");
  useEffect(() => {
    reportStatusRef.current = reportStatus;
  }, [reportStatus]);

  // Dados estáticos (mudam só quando você cria/remove mensagem ou importa grupo).
  const loadLists = useCallback(async () => {
    try {
      const [t, g] = await Promise.all([api.listTemplates(), api.listContactGroupTags()]);
      setMessages(t.filter((x) => x.active));
      setGroupTags(g);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }, []);

  // Dados "ao vivo" (mudam conforme o dispatcher processa) — atualizados no timer.
  const loadLive = useCallback(async () => {
    // allSettled: um blip num endpoint não derruba os outros três nem mostra erro à toa.
    // Cada pedaço atualiza sozinho; só sinaliza erro se TUDO falhar (API provavelmente fora).
    const [s, r, st, w] = await Promise.allSettled([
      api.dispatchStats(),
      api.dispatchReport(reportStatusRef.current || undefined),
      api.dispatchStatus(),
      api.warmupStatus(),
    ]);
    if (s.status === "fulfilled") setStats(s.value);
    if (r.status === "fulfilled") setReport(r.value);
    if (st.status === "fulfilled") {
      setPaused(st.value.paused);
      setCircuitOpenUntil(st.value.circuitOpen ? st.value.circuitOpenUntil : null);
    }
    if (w.status === "fulfilled") setWarmup(w.value);
    if ([s, r, st, w].every((x) => x.status === "rejected")) {
      const ex = (s as PromiseRejectedResult).reason;
      setError(ex instanceof Error ? ex.message : String(ex));
    } else {
      setError(null);
    }
  }, []);

  // Quantos contatos receberiam, com o público/grupo escolhido (mesmo filtro do disparo).
  useEffect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const { count } = await api.audienceCount({
          engagedOnly: audience === "responded" ? true : undefined,
          groupTag: group.trim() || undefined,
        });
        if (!cancelled) setAudienceCount(count);
      } catch {
        if (!cancelled) setAudienceCount(null);
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [audience, group]);


  useEffect(() => {
    // Bootstrap + polling de dados ao vivo (sistema externo): setState assíncrono pós-await,
    // não cascateia render — uso legítimo de efeito.
    /* eslint-disable react-hooks/set-state-in-effect */
    void loadLists();
    void loadLive();
    /* eslint-enable react-hooks/set-state-in-effect */
    const handle = setInterval(loadLive, 5_000);
    return () => clearInterval(handle);
  }, [loadLists, loadLive]);

  // Mantém a seleção única consistente. NÃO toca em selectedMessageId quando messages
  // chega vazia — esse estado acontece no mount inicial (antes do loadLists terminar) e
  // também quando o usuário apaga todas. Em ambos os casos a falta da seleção é tratada
  // por `selectedIds` (que valida se o id ainda está na lista) — sem mexer no localStorage.
  // Se messages vem populada e a selecionada (vinda do localStorage ou anterior) não está
  // mais lá, cai pra primeira disponível.
  useEffect(() => {
    if (messages.length === 0) return;
    setSelectedMessageId((prev) =>
      prev !== null && messages.some((m) => m.id === prev) ? prev : messages[0].id,
    );
  }, [messages]);

  // Persiste a seleção entre F5/reabrir o navegador. Falha silenciosa em modo privado
  // ou cota cheia — só atrapalha a persistência, não o disparo.
  useEffect(() => {
    if (typeof window === "undefined") return;
    try {
      if (selectedMessageId === null) {
        window.localStorage.removeItem(SELECTED_MESSAGE_STORAGE_KEY);
      } else {
        window.localStorage.setItem(SELECTED_MESSAGE_STORAGE_KEY, selectedMessageId);
      }
    } catch {
      // ignore: localStorage indisponível não compromete o fluxo do disparo
    }
  }, [selectedMessageId]);

  // Reaplica o filtro do relatório imediatamente quando muda o status selecionado.
  useEffect(() => {
    void loadLive();
  }, [reportStatus, loadLive]);

  // Quando o teto deixa de estar batido (liberou mais, ou virou o dia), libera o modal pra
  // reaparecer se bater o novo teto. "Cancelar" mantém dismissed porque atCap segue true.
  // Ajuste durante a render (em vez de setState-in-effect); o guard !capDismissed evita loop.
  if (warmup && !warmup.atCap && capDismissed) setCapDismissed(false);

  // Fim do envio: a fila tinha contatos e zerou enquanto enviava (não pausado, não foi
  // limpar/renovar). Mostra o modal de conclusão uma vez.
  useEffect(() => {
    const pending = stats?.pending ?? 0;
    const prev = prevPendingRef.current;
    prevPendingRef.current = pending;
    if (prev > 0 && pending === 0) {
      if (suppressDoneRef.current || paused) {
        suppressDoneRef.current = false;
        return;
      }
      setShowDone(true);
    }
  }, [stats, paused]);

  function countFor(key: DispatchJobStatus): number {
    if (!stats) return 0;
    return key === "Pending" ? stats.pending
      : key === "Sent" ? stats.sent
      : key === "Failed" ? stats.failed
      : stats.skipped;
  }

  async function addMessage() {
    const text = draft.trim();
    if (!text) return;
    setAdding(true);
    try {
      const created = await api.createTemplate(text, "Greeting");
      setDraft(DEFAULT_DRAFT);
      await loadLists();
      // Auto-seleciona a mensagem recém-criada. O usuário acabou de escrevê-la — quase
      // certamente é o que quer usar no próximo disparo (se quisesse a anterior, não
      // teria criado outra). Sem isso, a seleção persistida em localStorage continuava
      // apontando pra mensagem antiga e o usuário disparava com o template errado.
      setSelectedMessageId(created.id);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setAdding(false);
    }
  }

  async function removeMessage(id: string) {
    try {
      await api.deleteTemplate(id);
      await loadLists();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  // Lista do payload do disparo: com seleção única, é zero ou uma posição. O backend aceita
  // pool[] e sorteia — pool de tamanho 1 sempre rende a mesma mensagem (que é o esperado aqui).
  // Valida que o id ainda existe nas mensagens — protege contra "ghost id" (ex.: id salvo
  // no localStorage de um deploy anterior, ou seleção apontando pra mensagem recém apagada
  // antes do useEffect cair pra próxima).
  const selectedIds =
    selectedMessageId !== null && messages.some((m) => m.id === selectedMessageId)
      ? [selectedMessageId]
      : [];
  const pendingCount = stats?.pending ?? 0;
  const totalJobs = stats ? stats.pending + stats.sent + stats.failed + stats.skipped : 0;

  // Tabela de resultados: busca por telefone/nome e paginação, tudo sobre o que já foi carregado.
  const REPORT_PAGE_SIZE = 25;
  const reportQuery = reportSearch.trim().toLowerCase();
  const filteredReport = reportQuery
    ? report.filter(
        (i) =>
          (i.phone ?? "").toLowerCase().includes(reportQuery) ||
          (i.name ?? "").toLowerCase().includes(reportQuery),
      )
    : report;
  const reportTotalPages = Math.max(1, Math.ceil(filteredReport.length / REPORT_PAGE_SIZE));
  // Clamp na leitura (sem setState): se o poll de 5s muda o total, a página nunca "estoura".
  const reportCurrentPage = Math.min(reportPage, reportTotalPages);
  const reportPageRows = filteredReport.slice(
    (reportCurrentPage - 1) * REPORT_PAGE_SIZE,
    reportCurrentPage * REPORT_PAGE_SIZE,
  );

  // Etapa 2: prepara a fila — pausa e enfileira os contatos (entram "Na fila", nada sai ainda).
  async function onPrepare() {
    if (selectedIds.length === 0) {
      setDispatchMsg("Escolha uma mensagem antes de preparar.");
      return;
    }
    if (audienceCount === 0) {
      setDispatchMsg("Nenhum contato pra esse público. Importe contatos ou mude o filtro.");
      return;
    }
    setDispatching(true);
    setDispatchMsg(null);
    // Nova fila: limpa eventual supressão pendente, senão ela poderia engolir o modal de
    // conclusão desta campanha (caso um clear/renovar anterior não tenha consumido a flag).
    suppressDoneRef.current = false;
    try {
      // O servidor prepara a fila JÁ pausada (atômico com os jobs), então nada sai até clicar
      // "Iniciar envios" — não precisa pausar pelo front.
      const result = await api.dispatch(selectedIds, {
        engagedOnly: audience === "responded" ? true : undefined,
        groupTag: group.trim() || undefined,
      });
      setPaused(true);
      setDispatchMsg(
        `${result.scheduled} contato(s) na fila. Revise em "Resultado dos envios" e clique "Iniciar envios".`,
      );
      await loadLive();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    } finally {
      setDispatching(false);
    }
  }

  // Etapa 3: libera a fila preparada — o dispatcher começa a enviar. Confirmado pelo modal.
  async function onStart() {
    setConfirmStart(false);
    try {
      await api.resumeDispatch();
      setPaused(false);
      setDispatchMsg("Envios iniciados — vão sair aos poucos, com intervalos.");
      await loadLive();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    }
  }

  // Cancela o que foi preparado (apaga os "Na fila"), sem enviar. Confirmado pelo modal.
  async function onClear() {
    setConfirmClear(false);
    suppressDoneRef.current = true; // zerar a fila aqui não é "conclusão"
    try {
      await api.clearQueue();
      setDispatchMsg("Fila limpa.");
      await loadLive();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    }
  }

  // Renova a lista: baixa o histórico atual em Excel (backup) e zera todos os resultados.
  // Confirmado pelo modal padrão.
  async function onRenew() {
    setConfirmRenew(false);
    suppressDoneRef.current = true; // zerar resultados aqui não é "conclusão"
    try {
      // Backup COMPLETO (ignora o filtro de status da tela) — o reset apaga tudo.
      const all = await api.dispatchReport(undefined, 5000);
      if (all.length > 0) {
        downloadDispatchReportXlsx(all);
      }
      await api.resetResults();
      setDispatchMsg("Lista renovada — resultados zerados (histórico salvo no Excel).");
      await loadLive();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  async function onStop() {
    try {
      await api.pauseDispatch();
      setPaused(true);
      setDispatchMsg("Envios parados.");
      await loadLive();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    }
  }

  // Reinicia o aquecimento (chip novo): a curva volta ao dia 0. Confirmado pelo modal.
  async function onRestartWarmup() {
    setConfirmRestartWarmup(false);
    try {
      await api.restartWarmup();
      setDispatchMsg("Aquecimento reiniciado — começando do dia 1 da curva.");
      await loadLive();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  // Quantidade extra digitada, normalizada pra inteiro positivo (o backend espera int).
  const extraParsed = Math.floor(Number(extraInput));
  const extraValid = Number.isFinite(extraParsed) && extraParsed > 0;

  // Libera envios acima do teto SÓ pra hoje. all=true → "disparar todos"; senão usa o input.
  async function releaseWarmup(all: boolean) {
    if (!all && !extraValid) {
      return;
    }
    const extra = all ? undefined : extraParsed;
    setReleasing(true);
    try {
      await api.releaseWarmup(all ? { all: true } : { extra });
      setCapDismissed(true); // fecha o modal; reabre só se bater o novo teto
      setExtraInput("");
      setDispatchMsg(
        all ? "Teto liberado: a fila inteira vai sair hoje." : `Liberados +${extra} envios pra hoje.`,
      );
      await loadLive();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setReleasing(false);
    }
  }

  // Mostra o modal quando: bateu o teto efetivo, ainda há fila, não está pausado e o
  // operador não cancelou hoje. (atCap já considera o bônus liberado.)
  const showCapModal =
    !!warmup && warmup.atCap && !paused && pendingCount > 0 && !capDismissed;
  // Aviso proporcional ao quão "frio" o chip está (início da curva = mais arriscado).
  const warmupRisk = warmup
    ? warmup.day <= 3
      ? { cls: "risk-high", text: "Chip ainda novo — risco ALTO de ban. Não recomendado." }
      : warmup.day < warmup.totalDays
        ? { cls: "risk-mid", text: "Chip em aquecimento — risco moderado. Use com cautela." }
        : { cls: "risk-low", text: "Chip já aquecido — risco menor, mas use com bom senso." }
    : { cls: "risk-mid", text: "" };

  const prepareLabel = dispatching
    ? "Preparando..."
    : audience === "responded"
      ? "Adicionar para disparar (quem respondeu)"
      : group.trim()
        ? "Adicionar para disparar (grupo)"
        : "Adicionar para disparar (todos)";

  return (
    <main className="campaigns-screen">
      <header className="campaigns-section">
        <h2>Disparo de mensagens</h2>
        <p className="muted">Cada contato recebe uma mensagem sorteada. O envio é automático e espaçado.</p>
      </header>

      {error && <p className="error">{error}</p>}

      <section className="campaigns-section">
        <h3>1 · Suas mensagens ({messages.length})</h3>
        <details className="help-toggle">
          <summary>Como escrever a mensagem (variar sem cair como spam)</summary>
          <p className="muted small">
            <strong>Como variar sem cair como spam:</strong> dentro de uma mensagem, use{" "}
            <code>{"{a|b|c}"}</code> — o sistema sorteia uma opção por contato (pode usar vários pontos no
            texto, ex.: <code>{"{Oi|Olá}"}</code>). <strong>Não</strong> separe a saudação em modelos
            diferentes ("Oi" num, "Olá" noutro): isso manda texto idêntico pra muita gente. Só crie um{" "}
            <strong>2º modelo</strong> se o <strong>corpo</strong> da mensagem for diferente (outro
            argumento), não pra trocar a saudação. Seus contatos não têm nome → prefira saudações sem nome.
            A linha de saída (<strong>"SAIR"</strong>) já vem pronta no campo — mantenha ela.
          </p>
          <p className="muted small">
            <strong>Link:</strong> é só colar no texto — o WhatsApp gera a prévia sozinho. Prefira o{" "}
            <strong>seu domínio</strong> (ex.: <code>seusite.com.br</code>); <strong>evite encurtadores</strong>{" "}
            (bit.ly etc.), que pesam mais como spam.
          </p>
        </details>
        {messages.length > 0 && (
          <>
            <p className="muted small pool-label">
              Salvas — escolha qual usar neste disparo ({messages.length} {messages.length === 1 ? "mensagem" : "mensagens"}):
            </p>
            <ul className="message-pool">
              {messages.map((m) => {
                const selected = selectedMessageId === m.id;
                return (
                  <li key={m.id} className={selected ? undefined : "unselected"}>
                    <input
                      type="radio"
                      name="message-pick"
                      className="message-check"
                      checked={selected}
                      onChange={() => setSelectedMessageId(m.id)}
                      title={selected ? "Mensagem que vai no disparo" : "Clique para usar esta mensagem"}
                    />
                    {m.hasImage && <TemplateThumb id={m.id} />}
                    <span className="message-pool-text">{m.contentSpintax}</span>
                    <button
                      type="button"
                      className="message-remove"
                      title="Remover esta mensagem"
                      onClick={() => void removeMessage(m.id)}
                    >
                      ×
                    </button>
                  </li>
                );
              })}
            </ul>
          </>
        )}
        <div className="message-add">
          <label className="message-add-label" htmlFor="new-message">
            Escrever nova mensagem {messages.length > 0 ? "(adiciona às suas mensagens)" : ""}
          </label>
          <textarea
            id="new-message"
            className="message-box"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            placeholder="Escreva uma mensagem (ex.: {Oi|Olá}! ... responda SAIR pra não receber.)"
            rows={4}
          />
          <button type="button" onClick={() => void addMessage()} disabled={adding || !draft.trim()}>
            {adding ? "Adicionando..." : "+ Adicionar mensagem"}
          </button>
        </div>
      </section>

      <section className="campaigns-section">
        <h3>2 · Para quem enviar</h3>
        <div className="audience-row">
          <label>
            <span>Público</span>
            <select value={audience} onChange={(e) => setAudience(e.target.value as "all" | "responded")}>
              <option value="all">Todos os contatos</option>
              <option value="responded">Só quem já respondeu</option>
            </select>
          </label>
          <label>
            <span>Grupo (opcional)</span>
            <select value={group} onChange={(e) => setGroup(e.target.value)}>
              <option value="">Todos os grupos</option>
              {groupTags.map((g) => (
                <option key={g.groupTag} value={g.groupTag}>
                  {g.groupTag} ({g.count})
                </option>
              ))}
            </select>
          </label>
        </div>
        <p className="muted small">Quem pediu pra sair nunca recebe.</p>
      </section>

      {warmup && (
        <section className="campaigns-section warmup-panel">
          <div className="warmup-head">
            <h3>
              Aquecimento do chip — dia {warmup.day} de {warmup.totalDays}
              {warmup.phone && <span className="warmup-phone"> · {warmup.phone}</span>}
            </h3>
            <button
              type="button"
              className="warmup-restart"
              onClick={() => setConfirmRestartWarmup(true)}
              title="Reforço manual: reinicia a curva pro dia 1. A troca de chip já reinicia sozinha ao reconectar."
            >
              Reiniciar aquecimento
            </button>
          </div>
          <div className="warmup-bar" role="progressbar" aria-valuenow={warmup.sentToday} aria-valuemax={warmup.effectiveLimit}>
            <span style={{ width: `${warmup.unlimitedToday ? 100 : Math.min(100, warmup.effectiveLimit > 0 ? (warmup.sentToday / warmup.effectiveLimit) * 100 : 0)}%` }} />
          </div>
          <p className="warmup-line">
            {warmup.unlimitedToday ? (
              <>Hoje: <strong>{warmup.sentToday}</strong> enviados · <strong>teto liberado</strong> (sem limite hoje)</>
            ) : (
              <>
                Hoje: <strong>{warmup.sentToday} / {warmup.effectiveLimit}</strong> enviados · faltam{" "}
                <strong>{warmup.remaining}</strong>
                {warmup.bonusToday > 0 && <> <span className="warmup-bonus">(+{warmup.bonusToday} liberado)</span></>}
                {warmup.nextLimit !== null && warmup.nextLimit > warmup.todayLimit && (
                  <> · amanhã sobe para <strong>{warmup.nextLimit}</strong></>
                )}
                {" "}· estabiliza em <strong>{warmup.plateauLimit}/dia</strong>
              </>
            )}
          </p>
          <details className="help-toggle">
            <summary>Como o teto protege o chip</summary>
            <p className="muted small">
              O disparo respeita esse teto automaticamente: ao bater o limite do dia, ele para sozinho e
              retoma amanhã com um número maior. Aumentar aos poucos é o que protege o número de ban.
            </p>
          </details>
        </section>
      )}

      <section className="campaigns-section">
        <h3>3 · Disparar</h3>
        {circuitOpenUntil && (
          <p className="circuit-banner">
            Envios pausados automaticamente por falhas seguidas. Retomam sozinhos às{" "}
            <strong>{new Date(circuitOpenUntil).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}</strong>.
          </p>
        )}
        {pendingCount === 0 ? (
          <>
            <p className={`audience-count${audienceCount === 0 ? " zero" : ""}`}>
              {audienceCount === null
                ? "Calculando alvo..."
                : audienceCount === 0
                  ? "Nenhum contato pra esse público. Importe na aba Grupos ou mude o filtro."
                  : `Vai preparar ${audienceCount} contato(s) pra disparo.`}
            </p>
            <button
              type="button"
              className="dispatch-btn"
              onClick={() => void onPrepare()}
              disabled={dispatching || selectedIds.length === 0 || audienceCount === 0}
            >
              {prepareLabel}
            </button>
          </>
        ) : paused ? (
          <>
            <p className="prepared-banner">
              {pendingCount} contato(s) na fila — revise em "Resultado dos envios" e inicie quando quiser.
            </p>
            <div className="dispatch-actions">
              <button type="button" className="dispatch-btn" onClick={() => setConfirmStart(true)}>
                Iniciar envios
              </button>
              <button type="button" className="clear-btn" onClick={() => setConfirmClear(true)}>
                Limpar fila
              </button>
            </div>
          </>
        ) : (
          <>
            <p className="sending-banner">Enviando — {pendingCount} restante(s). Saindo aos poucos.</p>
            <button type="button" className="pause-btn" onClick={() => void onStop()}>
              Parar envios
            </button>
          </>
        )}
        {dispatchMsg && <p className="dispatch-msg">{dispatchMsg}</p>}
      </section>

      <section className="campaigns-section report-section">
        <div className="report-head">
          <h3>
            Resultado dos envios{reportStatus ? ` · ${DISPATCH_STATUS_LABELS[reportStatus]}` : ""}
          </h3>
          <div className="report-actions">
            <button
              type="button"
              className="report-export"
              onClick={() => downloadDispatchReportXlsx(report)}
              disabled={report.length === 0}
            >
              Baixar relatório (Excel)
            </button>
            <button
              type="button"
              className="report-renew"
              onClick={() => setConfirmRenew(true)}
              disabled={totalJobs === 0}
              title="Baixa o histórico em Excel e zera os resultados pra começar uma nova campanha"
            >
              Renovar lista
            </button>
          </div>
        </div>
        {stats && (
          <div className="dispatch-stats">
            <button
              type="button"
              className={`stat-chip stat-all chip-btn${reportStatus === "" ? " active" : ""}`}
              onClick={() => { setReportStatus(""); setReportPage(1); }}
              title="Mostrar todos os envios (limpa o filtro)"
            >
              Todos: {totalJobs}
            </button>
            {STAT_CHIPS.map((c) => (
              <button
                key={c.key}
                type="button"
                className={`stat-chip ${c.cls} chip-btn${reportStatus === c.key ? " active" : ""}`}
                onClick={() => { setReportStatus(reportStatus === c.key ? "" : c.key); setReportPage(1); }}
                title="Clique para ver/filtrar esses contatos abaixo"
              >
                {c.label}: {countFor(c.key)}
              </button>
            ))}
          </div>
        )}
        <p className="muted small">
          {reportStatus
            ? "Mostrando só os contatos desse status. Clique no chip de novo pra ver todos."
            : "Mostrando todos os envios. Clique num chip de status acima pra filtrar."}
        </p>
        {report.length > 0 && (
          <input
            type="search"
            className="report-search"
            placeholder="Buscar por telefone ou nome"
            value={reportSearch}
            onChange={(e) => {
              setReportSearch(e.target.value);
              setReportPage(1);
            }}
          />
        )}
        <div className="report-results">
        {report.length === 0 ? (
          <p className="muted">Nenhum envio ainda.</p>
        ) : reportPageRows.length === 0 ? (
          <p className="muted">Nenhum contato encontrado para "{reportSearch.trim()}".</p>
        ) : (
          <table className="contacts-table">
            <thead>
              <tr>
                <th>Telefone</th>
                <th>Nome</th>
                <th>Status</th>
                <th>Quando</th>
                <th>Erro</th>
              </tr>
            </thead>
            <tbody>
              {reportPageRows.map((i, idx) => (
                <tr key={(reportCurrentPage - 1) * REPORT_PAGE_SIZE + idx}>
                  <td className="mono">{i.phone || <span className="muted">-</span>}</td>
                  <td>{i.name || <span className="muted">-</span>}</td>
                  <td>
                    <span className={`stat-chip stat-${i.status.toLowerCase()}`}>
                      {DISPATCH_STATUS_LABELS[i.status]}
                    </span>
                  </td>
                  <td>{new Date(i.sentAt ?? i.scheduledAt).toLocaleString()}</td>
                  <td className="muted small">{i.errorReason || "-"}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        </div>
        {filteredReport.length > REPORT_PAGE_SIZE && (
          <div className="report-pager">
            <button
              type="button"
              onClick={() => setReportPage(reportCurrentPage - 1)}
              disabled={reportCurrentPage <= 1}
            >
              ‹ Anterior
            </button>
            <span className="muted small">
              Página {reportCurrentPage} de {reportTotalPages} · {filteredReport.length} contato(s)
            </span>
            <button
              type="button"
              onClick={() => setReportPage(reportCurrentPage + 1)}
              disabled={reportCurrentPage >= reportTotalPages}
            >
              Próxima ›
            </button>
          </div>
        )}
      </section>

      {showCapModal && warmup && (
        <div className="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="cap-title">
          <div className="modal-card">
            <h3 id="cap-title">Limite de aquecimento de hoje atingido</h3>
            <p className="cap-stats">
              Enviados hoje: <strong>{warmup.sentToday} de {warmup.effectiveLimit}</strong> · Na fila:{" "}
              <strong>{pendingCount}</strong>
            </p>
            <p className={`cap-risk ${warmupRisk.cls}`}>
              Dia {warmup.day} de {warmup.totalDays} — {warmupRisk.text}
            </p>
            <p className="muted small">
              O envio parou no teto de hoje. Você pode liberar mais (decisão sua) — a liberação vale
              só pra hoje e o aquecimento volta sozinho amanhã.
            </p>
            <div className="cap-actions">
              <div className="cap-extra">
                <input
                  type="number"
                  min={1}
                  step={1}
                  inputMode="numeric"
                  placeholder="quantos a mais"
                  value={extraInput}
                  onChange={(e) => setExtraInput(e.target.value)}
                />
                <button
                  type="button"
                  className="dispatch-btn"
                  disabled={releasing || !extraValid}
                  onClick={() => void releaseWarmup(false)}
                >
                  {extraValid ? `Liberar +${extraParsed}` : "Liberar +N"}
                </button>
              </div>
              <button
                type="button"
                className="cap-all-btn"
                disabled={releasing}
                onClick={() => void releaseWarmup(true)}
              >
                Disparar todos ({pendingCount})
              </button>
              <button
                type="button"
                className="clear-btn"
                disabled={releasing}
                onClick={() => setCapDismissed(true)}
              >
                Cancelar — retomo amanhã
              </button>
            </div>
          </div>
        </div>
      )}

      {confirmClear && (
        <ConfirmDialog
          title="Limpar a fila?"
          message={
            <>
              Vai remover os <strong>{pendingCount}</strong> contato(s) que estão <strong>"Na fila"</strong>. Eles{" "}
              <strong>não</strong> serão enviados.
              <br />
              <br />
              Os contatos e os envios já feitos não são afetados — dá pra preparar a fila de novo quando quiser.
            </>
          }
          confirmLabel="Sim, limpar"
          cancelLabel="Cancelar"
          danger
          onConfirm={() => void onClear()}
          onCancel={() => setConfirmClear(false)}
        />
      )}

      {confirmRenew && (
        <ConfirmDialog
          title="Renovar a lista?"
          message={
            <>
              Vai <strong>baixar o histórico atual em Excel</strong> (backup) e <strong>zerar todos os
              resultados</strong> — enviadas, falhas e fila — pra começar uma nova campanha.
              <br />
              <br />
              Os <strong>contatos não são afetados</strong>. O histórico zerado fica salvo na planilha.
            </>
          }
          confirmLabel="Sim, renovar"
          cancelLabel="Cancelar"
          danger
          onConfirm={() => void onRenew()}
          onCancel={() => setConfirmRenew(false)}
        />
      )}

      {confirmStart && (
        <ConfirmDialog
          title="Iniciar os envios?"
          message={
            <>
              Vai começar a enviar para os <strong>{pendingCount}</strong> contato(s) na fila. As mensagens saem{" "}
              <strong>aos poucos</strong>, com intervalos, respeitando o teto do aquecimento.
            </>
          }
          confirmLabel="Sim, iniciar"
          cancelLabel="Cancelar"
          onConfirm={() => void onStart()}
          onCancel={() => setConfirmStart(false)}
        />
      )}

      {showDone && (
        <div className="modal-overlay" role="dialog" aria-modal="true" aria-labelledby="done-title">
          <div className="modal-card">
            <h3 id="done-title">Disparo concluído</h3>
            <p>Todos os contatos da fila foram processados.</p>
            {stats && (
              <p className="cap-stats">
                Enviadas: <strong>{stats.sent}</strong> · Falharam: <strong>{stats.failed}</strong> · Puladas:{" "}
                <strong>{stats.skipped}</strong>
              </p>
            )}
            <p className="muted small">
              Os números acima são o total acumulado. Use "Renovar lista" pra zerar e começar uma nova campanha.
            </p>
            <div className="cap-actions">
              <button type="button" className="dispatch-btn" onClick={() => setShowDone(false)}>
                Ok
              </button>
            </div>
          </div>
        </div>
      )}

      {confirmRestartWarmup && (
        <ConfirmDialog
          title="Reiniciar o aquecimento?"
          message={
            <>
              O limite diário volta ao <strong>começo da curva</strong> (envios baixos) e sobe aos poucos de novo.
              <br />
              <br />
              Trocar de chip pelo QR já reinicia sozinho — use isto só pra forçar manualmente (ex.: esfriar um chip
              que levou aviso).
            </>
          }
          confirmLabel="Sim, reiniciar"
          cancelLabel="Cancelar"
          onConfirm={() => void onRestartWarmup()}
          onCancel={() => setConfirmRestartWarmup(false)}
        />
      )}
    </main>
  );
}
