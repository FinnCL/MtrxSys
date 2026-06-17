import { useEffect, useState } from "react";
import { api, ApiError } from "../api/client";
import type { GroupLink, GroupLinkStatus } from "../api/types";
import { downloadContactsXlsx } from "../utils/exportContacts";

const PAGE_SIZE = 25;

const STATUS_LABEL: Record<GroupLinkStatus, string> = {
  Found: "Resolvendo…",
  Resolved: "Pronto",
  Invalid: "Inválido",
  Joined: "Entrou",
  Imported: "Importado",
  Foreign: "Estrangeiro",
};

// Contagem regressiva m:ss (ex.: "2:05"; abaixo de 1 min mostra "45s").
function fmtCountdown(seconds: number): string {
  if (seconds < 60) return `${seconds}s`;
  return `${Math.floor(seconds / 60)}:${String(seconds % 60).padStart(2, "0")}`;
}

export function CollectorScreen() {
  // Restaura a última busca ao voltar pra aba (senão o componente remonta e a lista some).
  const [keyword, setKeyword] = useState(() => localStorage.getItem("collector.keyword") ?? "");
  const [activeKeyword, setActiveKeyword] = useState(() => localStorage.getItem("collector.keyword") ?? "");
  const [links, setLinks] = useState<GroupLink[]>([]);
  const [total, setTotal] = useState(0);
  const [page, setPage] = useState(0);
  const [loading, setLoading] = useState(true);
  const [collecting, setCollecting] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState<string | null>(null);
  const [busyCode, setBusyCode] = useState<string | null>(null);
  // Bumpar dispara o recarregamento da página atual (busca, polling, ações, "Atualizar").
  const [reloadTick, setReloadTick] = useState(0);
  // Entrada manual de links (colar um por linha).
  const [manualText, setManualText] = useState("");
  const [manualBusy, setManualBusy] = useState(false);
  // Info do motor de busca (nome + requisições feitas + último erro), só pra exibir no card.
  const [searchInfo, setSearchInfo] = useState<{
    engine: string;
    requestCount: number;
    lastError: string | null;
  } | null>(null);
  // Estado da trava anti-ban de entrada (limite explícito no painel).
  const [joinStatus, setJoinStatus] = useState<{
    joinsToday: number;
    maxPerDay: number;
    remaining: number;
    waitSeconds: number;
  } | null>(null);
  // Modal de bloqueio anti-ban ao tentar entrar rápido demais (countdown até liberar).
  const [joinBlock, setJoinBlock] = useState<{ secondsLeft: number; daily: boolean } | null>(null);

  const totalPages = Math.max(1, Math.ceil(total / PAGE_SIZE));

  // Conta regressiva do bloqueio anti-ban: decrementa 1s; o modal libera quando zera.
  useEffect(() => {
    if (!joinBlock || joinBlock.daily || joinBlock.secondsLeft <= 0) return;
    const id = window.setTimeout(() => {
      setJoinBlock((b) => (b && !b.daily ? { ...b, secondsLeft: b.secondsLeft - 1 } : b));
    }, 1000);
    return () => window.clearTimeout(id);
  }, [joinBlock]);

  // Persiste só a BUSCA ativa (não a página): ao sair e voltar pra aba, recarrega o que você
  // procurou, sempre na 1ª página. Persistir a página causava offset além dos resultados numa
  // busca menor → lista vazia com paginador.
  useEffect(() => {
    if (activeKeyword) localStorage.setItem("collector.keyword", activeKeyword);
    else localStorage.removeItem("collector.keyword");
  }, [activeKeyword]);

  // Carrega a info do motor de busca + estado da trava de entrada — atualiza junto com a lista/ações.
  useEffect(() => {
    let cancelled = false;
    api
      .collectorSearchInfo()
      .then((info) => {
        if (!cancelled)
          setSearchInfo({ engine: info.engine, requestCount: info.requestCount, lastError: info.lastError });
      })
      .catch(() => {
        /* info é só decorativa: silencia falha */
      });
    api
      .collectorJoinStatus()
      .then((s) => {
        if (!cancelled) setJoinStatus(s);
      })
      .catch(() => {
        /* idem */
      });
    return () => {
      cancelled = true;
    };
  }, [reloadTick]);

  // Conta regressiva AO VIVO do "aguarde" no card — tica de 1 em 1s, sem depender de refresh.
  useEffect(() => {
    if (!joinStatus || joinStatus.waitSeconds <= 0) return;
    const id = window.setTimeout(() => {
      setJoinStatus((s) => (s && s.waitSeconds > 0 ? { ...s, waitSeconds: s.waitSeconds - 1 } : s));
    }, 1000);
    return () => window.clearTimeout(id);
  }, [joinStatus]);

  // Ressincroniza o status de entrada com o servidor a cada 15s (pega novas entradas e a virada do
  // dia sem o usuário precisar atualizar a página). O tick acima suaviza entre as sincronizações.
  useEffect(() => {
    const id = window.setInterval(() => {
      api.collectorJoinStatus().then(setJoinStatus).catch(() => {});
    }, 15000);
    return () => window.clearInterval(id);
  }, []);

  // Carrega SÓ a página atual (paginação server-side): payload pequeno e render limitado.
  useEffect(() => {
    let cancelled = false;
    async function load() {
      // Sem busca ativa, a lista fica VAZIA (não despeja o acúmulo antigo) — só mostra o que você busca.
      if (!activeKeyword) {
        setLinks([]);
        setTotal(0);
        setLoading(false);
        return;
      }
      setLoading(true);
      try {
        const res = await api.collectorLinks({
          keyword: activeKeyword,
          limit: PAGE_SIZE,
          offset: page * PAGE_SIZE,
        });
        if (!cancelled) {
          // Defensivo: se a página atual passou do fim (itens vazios mas total>0), volta pra 1ª e
          // recarrega — em vez de mostrar lista vazia com paginador.
          if (page > 0 && res.items.length === 0 && res.total > 0) {
            setPage(0);
            return;
          }
          setLinks(res.items);
          setTotal(res.total);
          setError(null);
        }
      } catch (ex) {
        if (!cancelled) setError(ex instanceof Error ? ex.message : String(ex));
      } finally {
        if (!cancelled) setLoading(false);
      }
    }
    void load();
    return () => {
      cancelled = true;
    };
  }, [activeKeyword, page, reloadTick]);

  // Enquanto "coletando", recarrega a página atual a cada 4s (a coleta roda em background).
  // Para sozinho após ~1min.
  useEffect(() => {
    if (!collecting) return;
    let ticks = 0;
    const id = window.setInterval(() => {
      ticks += 1;
      setReloadTick((t) => t + 1);
      // Busca por nicho agora resolve só os próprios achados (rápida). ~40s cobre com folga.
      if (ticks >= 10) {
        setCollecting(false);
        setNotice(
          "Busca concluída. Se não apareceu nada, o nicho rendeu poucos links vivos — tente outro termo ou cole links no campo abaixo.",
        );
      }
    }, 4000);
    return () => window.clearInterval(id);
  }, [collecting]);

  async function search() {
    const kw = keyword.trim();
    setNotice(null);
    setError(null);
    try {
      const res = await api.collectorCollect(kw || undefined);
      setActiveKeyword(kw);
      setPage(0);
      setReloadTick((t) => t + 1);
      setCollecting(true);
      setNotice(
        kw
          ? res.searchConfigured
            ? `Buscando grupos de "${kw}" na web… os resultados aparecem aos poucos.`
            : `Busca por nicho indisponível (SearXNG fora do ar). Mostrando grupos variados do Telegram.`
          : "Buscando grupos variados… os resultados aparecem aos poucos.",
      );
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }

  // Entrada manual: cola links (um por linha) → valida na hora → só os vivos viram "Pronto".
  async function addManual() {
    const lines = manualText
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter(Boolean);
    if (lines.length === 0) return;
    setManualBusy(true);
    setNotice(null);
    setError(null);
    try {
      const tag = keyword.trim() || "manual";
      const r = await api.collectorAddManual(lines, tag);
      setManualText("");
      setActiveKeyword(tag); // mostra os recém-adicionados sob a tag/nicho
      setPage(0);
      setReloadTick((t) => t + 1);
      const parts = [`${r.live} válido(s)`, `${r.dead} morto(s)`];
      if (r.duplicates) parts.push(`${r.duplicates} já existia(m)`);
      if (r.pendingValidation) parts.push(`${r.pendingValidation} pendente(s) — WhatsApp offline`);
      setNotice(`Links processados: ${parts.join(" · ")}.`);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setManualBusy(false);
    }
  }

  async function join(link: GroupLink) {
    setBusyCode(link.inviteCode);
    setNotice(null);
    try {
      const r = await api.collectorJoin(link.inviteCode);
      const groupLabel = r.name || link.groupName || "grupo";
      setNotice(
        r.canImport
          ? `Entrou em "${groupLabel}". Já dá pra importar os contatos.`
          : `Entrou em "${groupLabel}", mas não consegui identificar o grupo p/ importar automaticamente — importe pela aba Grupos.`,
      );
      setReloadTick((t) => t + 1);
    } catch (ex) {
      if (ex instanceof ApiError && ex.status === 429) {
        // Trava anti-ban: abre o modal com a contagem regressiva real (tempo vindo do /join-status).
        try {
          const s = await api.collectorJoinStatus();
          setJoinStatus(s);
          setJoinBlock({ secondsLeft: s.waitSeconds, daily: s.remaining <= 0 });
        } catch {
          setJoinBlock({ secondsLeft: 0, daily: false });
        }
      } else if (ex instanceof ApiError) {
        setNotice(ex.message);
        if (ex.status === 422) setReloadTick((t) => t + 1); // link virou Inválido → some da lista
      } else {
        setNotice(`Falha ao entrar: ${String(ex)}`);
      }
    } finally {
      setBusyCode(null);
    }
  }

  async function importContacts(link: GroupLink) {
    if (!link.whatsAppGroupId) return;
    setBusyCode(link.inviteCode);
    setNotice(null);
    try {
      const tag = link.groupName?.trim() || link.whatsAppGroupId;
      const result = await api.importGroup(link.whatsAppGroupId, tag);
      setNotice(`${result.imported} importados · ${result.duplicated} duplicados de "${tag}".`);
      try {
        const saved = await api.listContacts({ groupTag: tag });
        if (saved.length > 0) downloadContactsXlsx(saved, tag);
      } catch {
        // download é extra
      }
      setReloadTick((t) => t + 1);
    } catch (ex) {
      setNotice(`Falha ao importar: ${ex instanceof Error ? ex.message : String(ex)}`);
    } finally {
      setBusyCode(null);
    }
  }

  if (loading && links.length === 0) return <div className="loading">Carregando coletor...</div>;

  return (
    <main className="groups-screen">
      <header className="groups-header">
        <div className="groups-header-row">
          <h2>Coletor de grupos</h2>
          {searchInfo && (
            <span className="muted small" title="Motor de busca ativo e requisições feitas (informativo)">
              Busca: {searchInfo.engine} · {searchInfo.requestCount} requisições
            </span>
          )}
          {joinStatus && (
            <span
              className="muted small"
              title="Trava anti-ban: teto diário de entradas e intervalo entre cada (protege o número de bloqueio)"
            >
              Entradas hoje: {joinStatus.joinsToday}/{joinStatus.maxPerDay}
              {joinStatus.remaining <= 0
                ? " · limite do dia atingido"
                : joinStatus.waitSeconds > 0
                  ? ` · aguarde ${fmtCountdown(joinStatus.waitSeconds)}`
                  : " · pode entrar"}
            </span>
          )}
        </div>
        <p className="muted">
          Digite um nicho (ex.: "bet") para buscar grupos de WhatsApp desse tema na web, ou deixe vazio
          para grupos variados de canais de Telegram. Entrar é manual, com trava anti-ban.
        </p>
        <div className="collector-search">
          <div className="search-input-wrap">
            <input
              type="text"
              placeholder='Nicho (ex.: "bet", "marketing"). Vazio = variado.'
              value={keyword}
              onChange={(e) => setKeyword(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter") void search();
              }}
            />
            {keyword && (
              <button
                type="button"
                className="search-clear"
                aria-label="Limpar"
                title="Limpar"
                onClick={() => setKeyword("")}
              >
                ×
              </button>
            )}
          </div>
          <button type="button" className="import-btn" onClick={() => void search()}>
            Buscar
          </button>
          <button
            type="button"
            className="icon-btn"
            onClick={() => setReloadTick((t) => t + 1)}
            disabled={loading}
            title="Atualizar"
            aria-label="Atualizar"
          >
            {loading ? "…" : "↻"}
          </button>
        </div>
        <div className="collector-manual">
          <label className="muted small" htmlFor="manual-links">
            Já tem links? Cole aqui (um por linha). O sistema valida e só os <strong>ativos</strong> entram:
          </label>
          <textarea
            id="manual-links"
            rows={3}
            placeholder={"https://chat.whatsapp.com/AbCdEf...\nhttps://chat.whatsapp.com/GhIjKl..."}
            value={manualText}
            onChange={(e) => setManualText(e.target.value)}
          />
          <button
            type="button"
            className="import-btn"
            onClick={() => void addManual()}
            disabled={manualBusy || manualText.trim().length === 0}
          >
            {manualBusy ? "Validando…" : "Validar e adicionar"}
          </button>
        </div>
      </header>

      {searchInfo?.lastError && (
        <p
          className="error"
          title="Motivo informado pelo motor de busca — por isso a busca pode parar de trazer resultados"
        >
          ⚠ {searchInfo.lastError}
        </p>
      )}
      {notice && <p className="muted">{notice}</p>}
      {error && <p className="error">{error}</p>}
      {total === 0 && !error && (
        <p className="muted">
          {activeKeyword
            ? `Nenhum grupo ativo encontrado para "${activeKeyword}".`
            : "Busque um nicho acima (ou cole links) para ver os grupos."}
        </p>
      )}

      <ul className="groups-list">
        {links.map((link) => {
          const busy = busyCode === link.inviteCode;
          // Anti-ban: enquanto QUALQUER entrada/importação está em curso, desabilita as ações de
          // TODAS as linhas. Sem isso, cliques rápidos em vários grupos lançariam joins concorrentes
          // que furam o INTERVALO da trava (Check acontece antes do RegisterJoin no servidor) — e é
          // o espaçamento entre entradas que protege o número de ban.
          const anyBusy = busyCode !== null;
          // Só os validados (Resolved) podem ser clicados — evita tentar entrar num link ainda
          // não-checado (Found) que pode estar morto. Found aparece como "Resolvendo…" desabilitado.
          const canJoin = link.status === "Resolved";
          const canImport = link.status === "Joined" && !!link.whatsAppGroupId;
          return (
            <li key={link.inviteCode} className="group-row">
              <div className="group-info">
                <span className="group-name">{link.groupName || link.inviteUrl}</span>
                <span className="muted small">
                  {link.participantCount !== null ? `${link.participantCount} participantes · ` : ""}
                  {STATUS_LABEL[link.status]}
                  {link.sourceChannel ? ` · via @${link.sourceChannel}` : ""}
                </span>
              </div>
              {link.status === "Imported" ? (
                <span className="import-summary">Importado</span>
              ) : canImport ? (
                <button
                  type="button"
                  className="import-btn"
                  onClick={() => void importContacts(link)}
                  disabled={anyBusy}
                >
                  {busy ? "Importando…" : "Importar contatos"}
                </button>
              ) : link.status === "Joined" ? (
                // Entrou, mas sem JID do grupo: não dá pra importar daqui (importaria 0). Orienta.
                <span className="muted small">Entrou · importe pela aba Grupos</span>
              ) : (
                <button
                  type="button"
                  className="import-btn"
                  onClick={() => void join(link)}
                  disabled={anyBusy || !canJoin}
                  title={canJoin ? "Entra no grupo pelo número conectado" : "Aguardando validação do link"}
                >
                  {busy ? "Entrando…" : "Entrar"}
                </button>
              )}
            </li>
          );
        })}
      </ul>

      {total > 0 && (
        <div className="collector-pager">
          <button
            type="button"
            className="btn-ghost"
            onClick={() => setPage((p) => Math.max(0, p - 1))}
            disabled={page <= 0}
          >
            ← Anterior
          </button>
          <span className="muted small">
            Página {page + 1} de {totalPages} · {total} resultados
          </span>
          <button
            type="button"
            className="btn-ghost"
            onClick={() => setPage((p) => Math.min(totalPages - 1, p + 1))}
            disabled={page >= totalPages - 1}
          >
            Próxima →
          </button>
        </div>
      )}

      {joinBlock && (
        <div className="modal-overlay" onClick={() => setJoinBlock(null)}>
          <div className="modal-dialog" role="dialog" aria-modal="true" onClick={(e) => e.stopPropagation()}>
            <h3>Espere para entrar no próximo (anti-ban)</h3>
            <div className="modal-body">
              {joinBlock.daily ? (
                <p>
                  Você atingiu o <strong>limite diário</strong> de entradas. Tente novamente amanhã — esse teto
                  é o que protege o número de bloqueio.
                </p>
              ) : joinBlock.secondsLeft > 0 ? (
                <p>
                  Entrar em vários grupos em sequência rápida arrisca o <strong>ban do número</strong>. Aguarde{" "}
                  <strong>{fmtCountdown(joinBlock.secondsLeft)}</strong> para entrar no próximo.
                </p>
              ) : (
                <p>✅ Pode entrar no próximo grupo agora.</p>
              )}
            </div>
            <div className="modal-actions">
              <button type="button" onClick={() => setJoinBlock(null)}>
                Entendi
              </button>
            </div>
          </div>
        </div>
      )}
    </main>
  );
}
