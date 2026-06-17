import { useCallback, useEffect, useRef, useState } from "react";
import { AuthProvider } from "./auth/AuthContext";
import { useAuth } from "./auth/useAuth";
import { LoginScreen } from "./components/LoginScreen";
import { ConversationList } from "./components/ConversationList";
import { ChatThread } from "./components/ChatThread";
import { ContactPanel } from "./components/ContactPanel";
import { WhatsAppOnboarding } from "./components/WhatsAppOnboarding";
import { GroupsScreen } from "./components/GroupsScreen";
import { CollectorScreen } from "./components/CollectorScreen";
import { ContactsScreen } from "./components/ContactsScreen";
import { CampaignsScreen } from "./components/CampaignsScreen";
import { ConfirmDialog } from "./components/ConfirmDialog";
import { api } from "./api/client";
import type { Conversation } from "./api/types";
import { emptyContactPaneMessage } from "./utils/chatLabels";
import "./App.css";

type ViewTab = "chat" | "collector" | "groups" | "contacts" | "campaigns";

// Persiste a aba ativa pra sobreviver ao F5/atualizar — sem isso, recarregar sempre cai no Chat.
const VIEW_TABS: ViewTab[] = ["chat", "collector", "groups", "contacts", "campaigns"];
function loadView(): ViewTab {
  const v = localStorage.getItem("app.view");
  return VIEW_TABS.includes(v as ViewTab) ? (v as ViewTab) : "chat";
}

function Shell() {
  const { user, ready, logout } = useAuth();
  const [selected, setSelected] = useState<Conversation | null>(null);
  const [wahaWorking, setWahaWorking] = useState<boolean | null>(null);
  const [syncing, setSyncing] = useState(false);
  const [syncMsg, setSyncMsg] = useState<string | null>(null);
  const [view, setView] = useState<ViewTab>(loadView);
  const [confirmDisconnect, setConfirmDisconnect] = useState(false);
  // Estável (useCallback) pra não recriar os efeitos do WhatsAppOnboarding (polling/auto-retry do
  // QR) a cada render deste pai — uma arrow inline mudaria de referência todo render.
  const handleWahaWorking = useCallback(() => setWahaWorking(true), []);
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

  // Desconecta o número do WhatsApp (não é o mesmo que "Sair" do sistema). Faz RESET completo
  // (logout + apaga a sessão + recria): sem isso, o WAHA restauraria o número antigo do volume e
  // não mostraria QR novo. Assim o pareamento é dinâmico — a sessão cai em ScanQrCode e a tela de
  // conexão reaparece sozinha (wahaWorking=false) pra escanear outro aparelho. Contatos no banco
  // ficam intactos; só a sessão do WhatsApp é zerada.
  const disconnectWhatsApp = useCallback(async () => {
    setConfirmDisconnect(false);
    try {
      const r = await api.wahaReset();
      autoSyncTriggeredRef.current = false; // permite o auto-sync de novo ao reconectar
      // Pós-reset a sessão fica em ScanQrCode/Starting (nunca Working) → mostra o onboarding.
      setWahaWorking(r.status === "Working");
    } catch (ex) {
      setSyncMsg(`Erro ao desconectar: ${ex instanceof Error ? ex.message : String(ex)}`);
      // Falha parcial (ex.: sessão apagada mas o start falhou): sincroniza a UI com o estado REAL
      // em vez de seguir mostrando "conectado" enganosamente. O auto-sync recria a sessão sozinho.
      await checkWaha();
    }
  }, [checkWaha]);

  useEffect(() => {
    if (!user) return;
    // Sincroniza com sistema externo (status do WAHA): o setState é assíncrono (pós-await),
    // não cascateia render — uso legítimo de efeito.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void checkWaha();
  }, [user, checkWaha]);

  // Se a sessão cair (inclusive por instabilidade no backend, não só pelo botão), rearma o
  // gatilho — assim reconcile/relink/sync re-rodam na próxima reconexão (a auto-cura).
  useEffect(() => {
    if (wahaWorking === false) autoSyncTriggeredRef.current = false;
  }, [wahaWorking]);

  // Lembra a aba ativa: ao atualizar a página, volta pra mesma aba (não cai sempre no Chat).
  useEffect(() => {
    localStorage.setItem("app.view", view);
  }, [view]);

  useEffect(() => {
    if (wahaWorking !== true) return;
    if (autoSyncTriggeredRef.current) return;
    autoSyncTriggeredRef.current = true;
    // Ao conectar, reconcilia o aquecimento com o número: se trocou de chip, reinicia sozinho.
    // E religa conversas órfãs ao contato (conserta as criadas durante instabilidade da sessão).
    // Melhor-esforço — uma falha aqui não pode travar o sync nem a tela.
    void api.reconcileWarmup().catch(() => {});
    void api.relinkConversations().catch(() => {});
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
              className={`tab-btn${view === "collector" ? " active" : ""}`}
              onClick={() => setView("collector")}
            >
              Coletor
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
        {wahaWorking === true && (
          <button type="button" className="disconnect-btn" onClick={() => setConfirmDisconnect(true)}>
            Desconectar WhatsApp
          </button>
        )}
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
        <WhatsAppOnboarding onWorking={handleWahaWorking} />
      ) : view === "collector" ? (
        <CollectorScreen />
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

      {confirmDisconnect && (
        <ConfirmDialog
          title="Desconectar o WhatsApp?"
          message={
            <>
              Desliga o número conectado e <strong>para os disparos</strong> até você parear outro celular pelo QR.
              <br />
              <br />
              Não é o mesmo que sair do sistema, e <strong>não afeta o WhatsApp do celular</strong>.
            </>
          }
          confirmLabel="Sim, desconectar"
          cancelLabel="Cancelar"
          danger
          onConfirm={() => void disconnectWhatsApp()}
          onCancel={() => setConfirmDisconnect(false)}
        />
      )}
    </div>
  );
}

export default function App() {
  // Presença pra a landing multi-ambiente: mantém um stream SSE aberto pro próprio backend
  // enquanto esta aba existir. Quem segura a conexão viva é o navegador, não um timer de JS
  // — então minimizar/segundo plano/congelar a aba NÃO destrava o card. Fechar/navegar/cair
  // derruba a conexão e o backend destrava sozinho. Independe de estar logado: "aba aberta"
  // trava o card. O EventSource reconecta sozinho se a conexão cair (ex.: API reiniciou).
  useEffect(() => {
    // Mesmo fallback do client.ts: o Ambiente A não injeta VITE_API_URL no build, então
    // sem isto a conexão de presença não abriria e o card A nunca travaria na landing.
    const apiUrl = (import.meta.env.VITE_API_URL as string | undefined) ?? "http://localhost:5080";
    const es = new EventSource(`${apiUrl}/api/presence/connect`);
    return () => es.close();
  }, []);

  return (
    <AuthProvider>
      <Shell />
    </AuthProvider>
  );
}
