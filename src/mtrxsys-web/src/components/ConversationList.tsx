import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { Conversation, ConversationCounts, ConversationStatus } from "../api/types";
import { conversationDisplayName, isLinkedId } from "../utils/chatLabels";

interface Props {
  selectedId: string | null;
  onSelect: (c: Conversation) => void;
}

// Abas por status. "Sem resposta" primeiro (é onde caem os disparos). Filtragem e contagem
// são feitas no servidor — o Chat só pede uma página por vez, então escala sem teto.
type FilterTab = ConversationStatus | "all";
const TABS: { key: FilterTab; label: string }[] = [
  { key: "awaitingReply", label: "Sem resposta" },
  { key: "responded", label: "Responderam" },
  { key: "optedOut", label: "Saíram" },
  { key: "all", label: "Todas" },
];
const PAGE = 50;
const EMPTY_COUNTS: ConversationCounts = { awaitingReply: 0, responded: 0, optedOut: 0, all: 0 };

export function ConversationList({ selectedId, onSelect }: Props) {
  const [items, setItems] = useState<Conversation[]>([]);
  const [counts, setCounts] = useState<ConversationCounts>(EMPTY_COUNTS);
  const [tab, setTab] = useState<FilterTab>("awaitingReply");
  const [search, setSearch] = useState("");
  const [debounced, setDebounced] = useState("");
  // Quantas mostrar: "Carregar mais" aumenta; trocar de aba/busca reseta. Buscamos sempre
  // de offset 0 até `limit` (substitui a lista) — simples e o polling atualiza a janela toda.
  const [limit, setLimit] = useState(PAGE);
  const [hasMore, setHasMore] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Debounce da busca para não bater na API a cada tecla.
  useEffect(() => {
    const h = setTimeout(() => setDebounced(search.trim()), 300);
    return () => clearTimeout(h);
  }, [search]);

  // Trocar de aba ou busca volta para a primeira página.
  useEffect(() => {
    setLimit(PAGE);
  }, [tab, debounced]);

  const statusParam = tab === "all" ? undefined : tab;
  const searchParam = debounced || undefined;

  // Lista da aba ativa (página de `limit`). A guarda `cancelled` evita que uma resposta
  // atrasada (ex.: troquei de aba) sobrescreva a lista atual com dados velhos.
  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      try {
        const list = await api.listConversations({ status: statusParam, search: searchParam, limit, offset: 0 });
        if (cancelled) return;
        setItems(list);
        setHasMore(list.length === limit);
        setError(null);
      } catch (ex) {
        if (!cancelled) setError(ex instanceof Error ? ex.message : String(ex));
      }
    };
    void run();
    const h = setInterval(run, 10_000);
    return () => {
      cancelled = true;
      clearInterval(h);
    };
  }, [statusParam, searchParam, limit]);

  // Contagens das abas: dependem só da busca (não da aba nem do "Carregar mais") — efeito
  // separado pra não refazer as 4 contagens à toa a cada troca de aba/página.
  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      try {
        const c = await api.getConversationCounts(searchParam);
        if (!cancelled) setCounts(c);
      } catch {
        /* contagem é secundária; ignora erro */
      }
    };
    void run();
    const h = setInterval(run, 10_000);
    return () => {
      cancelled = true;
      clearInterval(h);
    };
  }, [searchParam]);

  return (
    <aside className="sidebar conversations">
      <div className="sidebar-header">
        <h2>Conversas</h2>
      </div>
      <input
        className="conv-search"
        type="search"
        placeholder="Buscar nome ou telefone…"
        value={search}
        onChange={(e) => setSearch(e.target.value)}
      />
      <div className="tabs conv-tabs">
        {TABS.map((t) => (
          <button
            key={t.key}
            type="button"
            className={`tab-btn${tab === t.key ? " active" : ""}`}
            onClick={() => setTab(t.key)}
          >
            {t.label} ({counts[t.key]})
          </button>
        ))}
      </div>
      {error && <p className="error">{error}</p>}
      <ul className="conversation-list">
        {items.length === 0 && !error && <li className="empty">Nenhuma conversa aqui</li>}
        {items.map((c) => (
          <li
            key={c.id}
            className={`conv-item${selectedId === c.id ? " selected" : ""}`}
            onClick={() => onSelect(c)}
          >
            <div className="conv-row1">
              <span className="conv-title">{conversationDisplayName(c)}</span>
              <span className="conv-time">{formatRelative(c.lastMessageAt ?? c.createdAt)}</span>
            </div>
            <div className="conv-preview">
              {isLinkedId(c.waChatId) && <span className="badge-private">privado</span>}
              {c.lastMessagePreview ?? "—"}
            </div>
          </li>
        ))}
        {hasMore && (
          <li className="conv-loadmore">
            <button type="button" onClick={() => setLimit((l) => l + PAGE)}>
              Carregar mais
            </button>
          </li>
        )}
      </ul>
    </aside>
  );
}

function formatRelative(iso: string): string {
  const d = new Date(iso);
  const diffMs = Date.now() - d.getTime();
  const minutes = Math.floor(diffMs / 60_000);
  if (minutes < 1) return "agora";
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;
  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d`;
  return d.toLocaleDateString();
}
