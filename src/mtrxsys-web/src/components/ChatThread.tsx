import { useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type { ChatMessage, Conversation } from "../api/types";
import { MessageComposer } from "./MessageComposer";
import { conversationDisplayName, conversationSubtitle } from "../utils/chatLabels";

interface Props {
  conversation: Conversation;
}

export function ChatThread({ conversation }: Props) {
  const [messages, setMessages] = useState<ChatMessage[]>([]);
  const [error, setError] = useState<string | null>(null);
  const bottomRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    let cancelled = false;
    async function load() {
      try {
        const resp = await api.getConversationMessages(conversation.id);
        if (!cancelled) {
          // API returns newest first; reverse for chat reading order
          setMessages([...resp.messages].reverse());
          setError(null);
        }
      } catch (ex) {
        if (!cancelled) setError(ex instanceof Error ? ex.message : String(ex));
      }
    }
    void load();
    const handle = setInterval(load, 4_000);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [conversation.id]);

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [messages]);

  async function onSend(text: string) {
    const sent = await api.sendMessage(conversation.id, text);
    setMessages((prev) => (prev.some((m) => m.id === sent.id) ? prev : [...prev, sent]));
  }

  return (
    <section className="chat-thread">
      <header className="chat-header">
        <h3>{conversationDisplayName(conversation)}</h3>
        <span className="chat-subtitle">{conversationSubtitle(conversation)}</span>
      </header>
      {error && <p className="error">{error}</p>}
      <div className="messages">
        {messages.map((m) => (
          <div key={m.id} className={`msg ${m.direction.toLowerCase()}`}>
            {m.authorPhone && m.direction === "Inbound" && conversation.isGroup && (
              <div className="msg-author">{m.authorPhone}</div>
            )}
            <div className="msg-body">{m.body || (m.mediaUrl ? "[mídia]" : "")}</div>
            <div className="msg-time">{new Date(m.timestamp).toLocaleTimeString()}</div>
          </div>
        ))}
        <div ref={bottomRef} />
      </div>
      <MessageComposer onSend={onSend} />
    </section>
  );
}
