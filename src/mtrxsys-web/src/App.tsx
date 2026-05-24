import { useCallback, useEffect, useRef, useState } from "react";
import { AuthProvider, useAuth } from "./auth/AuthContext";
import { LoginScreen } from "./components/LoginScreen";
import { ConversationList } from "./components/ConversationList";
import { ChatThread } from "./components/ChatThread";
import { ContactPanel } from "./components/ContactPanel";
import { WhatsAppOnboarding } from "./components/WhatsAppOnboarding";
import { GroupsScreen } from "./components/GroupsScreen";
import { ContactsScreen } from "./components/ContactsScreen";
import { CampaignsScreen } from "./components/CampaignsScreen";
import { api } from "./api/client";
import type { Conversation } from "./api/types";
import { emptyContactPaneMessage } from "./utils/chatLabels";
import "./App.css";

type ViewTab = "chat" | "groups" | "contacts" | "campaigns";

function Shell() {
  const { user, ready, logout } = useAuth();
  const [selected, setSelected] = useState<Conversation | null>(null);
  const [wahaWorking, setWahaWorking] = useState<boolean | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [syncMsg, setSyncMsg] = useState<string | null>(null);
  const [view, setView] = useState<ViewTab>("chat");
  const autoSyncTriggeredRef = useRef(false);

  const runSync = useCallback(async (silent = false) => {
    setSyncing(true);
    if (!silent) setSyncMsg(null);
    try {
      const result = await api.wahaSync();
      setSyncMsg(`${result.chatsTouched} chats, ${result.messagesImported} msgs novas, ${result.contactsCreated} contatos`);
    } catch (ex) {
      setSyncMsg(`Erro no sync: ${ex instanceof Error ? ex.message : String(ex)}`);
    } finally {
      setSyncing(false);
      setTimeout(() => setSyncMsg(null), 5000);
    }
  }, []);

  const checkWaha = useCallback(async () => {
    try {
      const resp = await api.wahaStatus();
      setWahaWorking(resp.status === "Working");
    } catch {
      setWahaWorking(false);
    }
  }, []);

  useEffect(() => {
    if (!user) return;
    void checkWaha();
  }, [user, checkWaha]);

  useEffect(() => {
    if (wahaWorking !== true) return;
    if (autoSyncTriggeredRef.current) return;
    autoSyncTriggeredRef.current = true;
    void runSync(true);
  }, [wahaWorking, runSync]);

  if (!ready) return <div className="loading">Carregando...</div>;
  if (!user) return <LoginScreen />;

  return (
    <div className="app-shell">
      <header className="topbar">
        <span className="brand">MtrxSys</span>
        {wahaWorking === true && (
          <nav className="tabs">
            <button
              type="button"
              className={`tab-btn${view === "chat" ? " active" : ""}`}
              onClick={() => setView("chat")}
            >
              Chat
            </button>
            <button
              type="button"
              className={`tab-btn${view === "groups" ? " active" : ""}`}
              onClick={() => setView("groups")}
            >
              Grupos
            </button>
            <button
              type="button"
              className={`tab-btn${view === "contacts" ? " active" : ""}`}
              onClick={() => setView("contacts")}
            >
              Contatos
            </button>
            <button
              type="button"
              className={`tab-btn${view === "campaigns" ? " active" : ""}`}
              onClick={() => setView("campaigns")}
            >
              Disparo
            </button>
          </nav>
        )}
        {wahaWorking === true && (
          <button type="button" onClick={() => void runSync(false)} disabled={syncing} className="sync-btn">
            {syncing ? "Sincronizando..." : "Sincronizar"}
          </button>
        )}
        {syncMsg && <span className="sync-msg">{syncMsg}</span>}
        <span className="who">
          {user.displayName} <span className="muted">({user.email})</span>
        </span>
        <button type="button" onClick={logout}>Sair</button>
      </header>
      {wahaWorking === null ? (
        <div className="loading">
          <p>Verificando WhatsApp...</p>
          <button type="button" className="text-link" onClick={logout}>
            Sair
          </button>
        </div>
      ) : !wahaWorking ? (
        <WhatsAppOnboarding onWorking={() => setWahaWorking(true)} />
      ) : view === "groups" ? (
        <GroupsScreen />
      ) : view === "contacts" ? (
        <ContactsScreen />
      ) : view === "campaigns" ? (
        <CampaignsScreen />
      ) : (
        <main className="three-col">
          <ConversationList selectedId={selected?.id ?? null} onSelect={setSelected} />
          {selected ? (
            <ChatThread conversation={selected} />
          ) : (
            <section className="chat-thread empty-pane">
              <p>Selecione uma conversa para começar</p>
            </section>
          )}
          {selected?.contactId ? (
            <ContactPanel contactId={selected.contactId} />
          ) : (
            <aside className="contact-panel empty-pane">
              <p>{selected ? emptyContactPaneMessage(selected) : "Selecione uma conversa"}</p>
            </aside>
          )}
        </main>
      )}
    </div>
  );
}

export default function App() {
  return (
    <AuthProvider>
      <Shell />
    </AuthProvider>
  );
}
