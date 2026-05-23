import { useEffect, useState } from "react";
import { api } from "../api/client";
import type { Conversation } from "../api/types";
import { conversationDisplayName, isLinkedId } from "../utils/chatLabels";

interface Props {
  selectedId: string | null;
  onSelect: (c: Conversation) => void;
}

export function ConversationList({ selectedId, onSelect }: Props) {
  const [items, setItems] = useState<Conversation[]>([]);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const list = await api.listConversations();
        if (!cancelled) {
          setItems(list);
          setError(null);
        }
      } catch (ex) {
        if (!cancelled) setError(ex instanceof Error ? ex.message : String(ex));
      }
    }
    void load();
    const handle = setInterval(load, 10_000);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, []);

  return (
    <aside className="sidebar conversations">
      <div className="sidebar-header">
        <h2>Conversas</h2>
      </div>
      {error && <p className="error">{error}</p>}
      <ul className="conversation-list">
        {items.length === 0 && !error && <li className="empty">Sem conversas ainda</li>}
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
