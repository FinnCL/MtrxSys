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

// Texto inicial do campo: já traz a saudação em spintax e a linha de saída (SAIR),
// que deve estar sempre presente. O usuário escreve o miolo da mensagem no meio.
const DEFAULT_DRAFT =
  "{Oi|Olá|E aí}, {tudo bem|tudo certo}? {Tenho uma novidade|Queria te mostrar uma coisa} {pra você|que pode te interessar}.\n\n{Entre no link e saiba mais|Dá uma olhada aqui|Confira os detalhes}: [cole seu link aqui]\n\nResponda SAIR para não receber mais mensagens.";

const IMAGE_TYPES = ["image/png", "image/jpeg", "image/webp"];
const MAX_IMAGE_BYTES = 2 * 1024 * 1024;

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
  // Mensagens DESmarcadas (excluídas do rodízio). Por padrão todas participam.
  const [excludedIds, setExcludedIds] = useState<Set<string>>(new Set());
  const [stats, setStats] = useState<DispatchStats | null>(null);
  const [report, setReport] = useState<DispatchReportItem[]>([]);
  const [reportStatus, setReportStatus] = useState<"" | DispatchJobStatus>("");
  const [draft, setDraft] = useState(DEFAULT_DRAFT);
  // Imagem opcional da nova mensagem (base64 + mimetype + URL local pra preview).
  const [image, setImage] = useState<{ base64: string; mimeType: string; previewUrl: string } | null>(null);
  const [imageError, setImageError] = useState<string | null>(null);
  const [audience, setAudience] = useState<"all" | "responded">("all");
  const [group, setGroup] = useState("");
  const [adding, setAdding] = useState(false);
  const [dispatching, setDispatching] = useState(false);
  const [dispatchMsg, setDispatchMsg] = useState<string | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [paused, setPaused] = useState(false);
  const [audienceCount, setAudienceCount] = useState<number | null>(null);
  const [warmup, setWarmup] = useState<WarmupStatus | null>(null);
  // Modal de teto atingido: input do "+N" e flag pra não reabrir depois de cancelar hoje.
  const [capDismissed, setCapDismissed] = useState(false);
  const [extraInput, setExtraInput] = useState("");
  const [releasing, setReleasing] = useState(false);

  const reportStatusRef = useRef<"" | DispatchJobStatus>("");
  reportStatusRef.current = reportStatus;

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
    try {
      const [s, r, st, w] = await Promise.all([
        api.dispatchStats(),
        api.dispatchReport(reportStatusRef.current || undefined),
        api.dispatchStatus(),
        api.warmupStatus(),
      ]);
      setStats(s);
      setReport(r);
      setPaused(st.paused);
      setWarmup(w);
      setError(null);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
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
    void loadLists();
    void loadLive();
    const handle = setInterval(loadLive, 5_000);
    return () => clearInterval(handle);
  }, [loadLists, loadLive]);

  // Reaplica o filtro do relatório imediatamente quando muda o status selecionado.
  useEffect(() => {
    void loadLive();
  }, [reportStatus, loadLive]);

  // Quando o teto deixa de estar batido (liberou mais, ou virou o dia), libera o modal pra
  // reaparecer se bater o novo teto. "Cancelar" mantém dismissed porque atCap segue true.
  useEffect(() => {
    if (warmup && !warmup.atCap) setCapDismissed(false);
  }, [warmup]);

  function countFor(key: DispatchJobStatus): number {
    if (!stats) return 0;
    return key === "Pending" ? stats.pending
      : key === "Sent" ? stats.sent
      : key === "Failed" ? stats.failed
      : stats.skipped;
  }

  // Lê a imagem escolhida como base64 (sem o prefixo data:...;base64,) e valida tipo/tamanho.
  // Limites espelham o backend: PNG/JPEG/WebP, até 2 MB.
  function onPickImage(file: File | null) {
    setImageError(null);
    if (!file) {
      setImage(null);
      return;
    }
    if (!IMAGE_TYPES.includes(file.type)) {
      setImageError("Use PNG, JPEG ou WebP.");
      return;
    }
    if (file.size > MAX_IMAGE_BYTES) {
      setImageError("Imagem acima de 2 MB. Reduza o tamanho.");
      return;
    }
    const reader = new FileReader();
    reader.onload = () => {
      const result = String(reader.result);
      const base64 = result.slice(result.indexOf(",") + 1);
      setImage({ base64, mimeType: file.type, previewUrl: result });
    };
    reader.onerror = () => setImageError("Não consegui ler o arquivo.");
    reader.readAsDataURL(file);
  }

  function clearImage() {
    setImage(null);
    setImageError(null);
  }

  async function addMessage() {
    const text = draft.trim();
    if (!text) return;
    setAdding(true);
    try {
      await api.createTemplate(
        text,
        "Greeting",
        image ? { base64: image.base64, mimeType: image.mimeType } : undefined,
      );
      setDraft(DEFAULT_DRAFT);
      clearImage();
      await loadLists();
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

  function toggleExclude(id: string) {
    setExcludedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }
      return next;
    });
  }

  const selectedIds = messages.filter((m) => !excludedIds.has(m.id)).map((m) => m.id);
  const pendingCount = stats?.pending ?? 0;
  const totalJobs = stats ? stats.pending + stats.sent + stats.failed + stats.skipped : 0;

  // Etapa 2: prepara a fila — pausa e enfileira os contatos (entram "Na fila", nada sai ainda).
  async function onPrepare() {
    if (selectedIds.length === 0) {
      setDispatchMsg("Marque ao menos uma mensagem antes de preparar.");
      return;
    }
    if (audienceCount === 0) {
      setDispatchMsg("Nenhum contato pra esse público. Importe contatos ou mude o filtro.");
      return;
    }
    setDispatching(true);
    setDispatchMsg(null);
    try {
      await api.pauseDispatch(); // garante que nada sai enquanto você revisa
      setPaused(true);
      const result = await api.dispatch(selectedIds, {
        engagedOnly: audience === "responded" ? true : undefined,
        groupTag: group.trim() || undefined,
      });
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

  // Etapa 3: libera a fila preparada — o dispatcher começa a enviar.
  async function onStart() {
    if (!window.confirm(`Iniciar o envio para ${pendingCount} contato(s) na fila?`)) {
      return;
    }
    try {
      await api.resumeDispatch();
      setPaused(false);
      setDispatchMsg("Envios iniciados — vão sair aos poucos, com intervalos.");
      await loadLive();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    }
  }

  // Cancela o que foi preparado (apaga os "Na fila"), sem enviar.
  async function onClear() {
    if (!window.confirm("Limpar a fila? Os contatos preparados não serão enviados.")) {
      return;
    }
    try {
      await api.clearQueue();
      setDispatchMsg("Fila limpa.");
      await loadLive();
    } catch (ex) {
      setDispatchMsg(`Erro: ${ex instanceof Error ? ex.message : String(ex)}`);
    }
  }

  // Renova a lista: baixa o histórico atual em Excel (backup) e zera todos os resultados.
  async function onRenew() {
    if (
      !window.confirm(
        "Renovar a lista? Vou baixar o histórico atual em Excel e zerar todos os resultados (enviadas, falhas, fila). Continuar?",
      )
    ) {
      return;
    }
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

  // Reinicia o aquecimento (chip novo): a curva volta ao dia 0 (envios baixos de novo).
  async function onRestartWarmup() {
    if (
      !window.confirm(
        "Reiniciar o aquecimento agora? O limite diário volta ao começo da curva (envios baixos) " +
          "e sobe aos poucos de novo. Obs.: trocar de chip pelo QR já reinicia sozinho ao reconectar — " +
          "use isto só para forçar manualmente (ex.: esfriar um chip que levou aviso).",
      )
    ) {
      return;
    }
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
        <p className="muted">
          Monte um conjunto de mensagens, escolha pra quem, e dispare. Cada contato recebe uma sorteada —
          sem texto repetido. O envio é automático e espaçado.
        </p>
      </header>

      {error && <p className="error">{error}</p>}

      {stats && (
        <div className="dispatch-stats">
          {STAT_CHIPS.map((c) => (
            <button
              key={c.key}
              type="button"
              className={`stat-chip ${c.cls} chip-btn${reportStatus === c.key ? " active" : ""}`}
              onClick={() => setReportStatus(reportStatus === c.key ? "" : c.key)}
              title="Clique para ver/filtrar esses contatos abaixo"
            >
              {c.label}: {countFor(c.key)}
            </button>
          ))}
        </div>
      )}


      <section className="campaigns-section">
        <h3>1 · Suas mensagens ({messages.length})</h3>
        <p className="muted small">
          <strong>Como variar sem cair como spam:</strong> dentro de uma mensagem, use{" "}
          <code>{"{a|b|c}"}</code> — o sistema sorteia uma opção por contato (pode usar vários pontos no
          texto, ex.: <code>{"{Oi|Olá}"}</code>). <strong>Não</strong> separe a saudação em modelos
          diferentes ("Oi" num, "Olá" noutro): isso manda texto idêntico pra muita gente. Só crie um{" "}
          <strong>2º modelo</strong> se o <strong>corpo</strong> da mensagem for diferente (outro
          argumento), não pra trocar a saudação. Seus contatos não têm nome → prefira saudações sem nome.
          A linha de saída (<strong>"SAIR"</strong>) já vem pronta no campo — mantenha ela.
        </p>
        {messages.length > 0 && (
          <>
            <p className="muted small pool-label">
              Salvas — marque quais entram no rodízio ({selectedIds.length} de {messages.length} selecionadas):
            </p>
            <ul className="message-pool">
              {messages.map((m) => {
                const selected = !excludedIds.has(m.id);
                return (
                  <li key={m.id} className={selected ? undefined : "unselected"}>
                    <input
                      type="checkbox"
                      className="message-check"
                      checked={selected}
                      onChange={() => toggleExclude(m.id)}
                      title={selected ? "No rodízio (desmarque para excluir)" : "Fora do rodízio"}
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
          <p className="muted small">
            <strong>Link:</strong> é só colar no texto acima — o WhatsApp gera a prévia sozinho. Prefira o{" "}
            <strong>seu domínio</strong> (ex.: <code>seusite.com.br</code>); <strong>evite encurtadores</strong>{" "}
            (bit.ly etc.), que pesam mais como spam.
          </p>
          <div className="message-image">
            <label className="message-image-label" htmlFor="new-image">
              Anexar imagem (opcional)
            </label>
            <input
              id="new-image"
              type="file"
              accept="image/png,image/jpeg,image/webp"
              onChange={(e) => onPickImage(e.target.files?.[0] ?? null)}
            />
            {image && (
              <div className="image-preview">
                <img src={image.previewUrl} alt="prévia da imagem" />
                <button type="button" className="message-remove" title="Remover imagem" onClick={clearImage}>
                  ×
                </button>
              </div>
            )}
            {imageError && <p className="error">{imageError}</p>}
            <p className="muted small">
              Uma imagem por mensagem (PNG, JPEG ou WebP, até 2 MB). Ela vai junto com o texto como{" "}
              <strong>legenda</strong> — a linha "SAIR" continua aparecendo. Imagem e link aumentam um pouco o
              risco de ban: use com moderação e evite chip novo/frio.
            </p>
          </div>
          <button type="button" onClick={() => void addMessage()} disabled={adding || !draft.trim()}>
            {adding ? "Adicionando..." : image ? "+ Adicionar mensagem com imagem" : "+ Adicionar mensagem"}
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
        <p className="muted small">
          "Só quem já respondeu" envia apenas pros contatos engajados — mais resultado e mais seguro. Quem
          pediu pra sair nunca recebe.
        </p>
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
              onClick={() => void onRestartWarmup()}
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
          <p className="muted small">
            O disparo respeita esse teto automaticamente: ao bater o limite do dia, ele para sozinho e
            retoma amanhã com um número maior. Aumentar aos poucos é o que protege o número de ban.
          </p>
        </section>
      )}

      <section className="campaigns-section">
        <h3>3 · Disparar</h3>
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
              <button type="button" className="dispatch-btn" onClick={() => void onStart()}>
                Iniciar envios
              </button>
              <button type="button" className="clear-btn" onClick={() => void onClear()}>
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
              onClick={() => void onRenew()}
              disabled={totalJobs === 0}
              title="Baixa o histórico em Excel e zera os resultados pra começar uma nova campanha"
            >
              Renovar lista
            </button>
          </div>
        </div>
        <p className="muted small">
          {reportStatus
            ? "Mostrando só os contatos desse status. Clique no chip de novo pra ver todos."
            : "Mostrando todos os envios. Clique num chip de status acima pra filtrar."}
        </p>
        {report.length === 0 ? (
          <p className="muted">Nenhum envio ainda.</p>
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
              {report.map((i, idx) => (
                <tr key={idx}>
                  <td className="mono">{i.phone ?? "—"}</td>
                  <td>{i.name || <span className="muted">—</span>}</td>
                  <td>
                    <span className={`stat-chip stat-${i.status.toLowerCase()}`}>
                      {DISPATCH_STATUS_LABELS[i.status]}
                    </span>
                  </td>
                  <td>{new Date(i.sentAt ?? i.scheduledAt).toLocaleString()}</td>
                  <td className="muted small">{i.errorReason ?? ""}</td>
                </tr>
              ))}
            </tbody>
          </table>
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
    </main>
  );
}
