import { useCallback, useEffect, useRef, useState } from "react";
import { AuthProvider } from "./auth/AuthContext";
import { useAuth } from "./auth/useAuth";
import { LoginScreen } from "./components/LoginScreen";
import { ConversationList } from "./components/ConversationList";
import { ChatThread } from "./components/ChatThread";
import { ContactPanel } from "./components/ContactPanel";
import { GroupsScreen } from "./components/GroupsScreen";
import { CollectorScreen } from "./components/CollectorScreen";
import { ContactsScreen } from "./components/ContactsScreen";
import { CampaignsScreen } from "./components/CampaignsScreen";
import { LivePhoneScreen } from "./components/LivePhoneScreen";
import { ConfirmDialog } from "./components/ConfirmDialog";
import { api } from "./api/client";
import type { Conversation } from "./api/types";
import { emptyContactPaneMessage } from "./utils/chatLabels";
import "./App.css";

// URL de uma tela embutível opcional na aba "Celular" (maquete fake A=:3000/B=:3001, ou o noVNC do
// Android real). Vazia = a aba "Celular" ainda aparece (o aparelho virtual é o WAHA); só não há tela
// embutida. É passada pra LivePhoneScreen como fallback visual.
const EMULATOR_URL = import.meta.env.VITE_EMULATOR_URL as string | undefined;
// "scrcpy" = a tela embutida é o ws-scrcpy do Android local (monta o stream direto). Vazio = embute a
// url como está (maquete fake / noVNC).
const EMULATOR_KIND = import.meta.env.VITE_EMULATOR_KIND as string | undefined;
// "1" = mostra a seção "Android em container (KVM)" na aba Celular. Só faz sentido no SERVIDOR (Linux
// com KVM). No localhost fica oculta (não há como provisionar Android aqui).
const PHONE_SERVER_OPTION = import.meta.env.VITE_PHONE_SERVER === "1";
// Device adb a espelhar na aba Celular (LDPlayer/emulador). Ex.: emulator-5554 (1ª instância do
// LDPlayer), emulator-5556 (2ª)… Configurável por ambiente. Vazio = emulator-5554.
const EMULATOR_UDID = import.meta.env.VITE_EMULATOR_UDID as string | undefined;

type ViewTab = "chat" | "collector" | "groups" | "contacts" | "campaigns" | "phone";

// Persiste a aba ativa pra sobreviver ao F5/atualizar — sem isso, recarregar sempre cai no Chat.
const VIEW_TABS: ViewTab[] = ["phone", "chat", "collector", "groups", "contacts", "campaigns"];
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
  const autoSyncTriggeredRef = useRef(false);
  // Na 1ª vez que detectar o WhatsApp desconectado, cai na aba "Celular" (onde fica a conexão/QR).
  const didInitViewRef = useRef(false);

  // Atalho da Fase Humana (aba Celular) → conversa na aba Chat. Troca de aba PRIMEIRO, pra a
  // resposta ser imediata; a conversa entra quando chegar. Busca com limit=1 só pra obter a
  // Conversation (o ChatThread carrega as mensagens sozinho). Se falhar, fica na lista pra escolher
  // à mão — um atalho que não abriu não é motivo pra quebrar a tela.
  const openConversation = useCallback(async (id: string) => {
    setView("chat");
    try {
      const { conversation } = await api.getConversationMessages(id, 1, 0);
      setSelected(conversation);
    } catch {
      /* segue na lista */
    }
  }, []);

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
    // não cascateia render — uso legítimo de efeito. Agora com POLLING (5s) — sem o antigo gate de
    // tela cheia, é este poll que detecta quando você pareia o WhatsApp (na aba "Celular").
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void checkWaha();
    const id = setInterval(() => void checkWaha(), 5000);
    return () => clearInterval(id);
  }, [user, checkWaha]);

  // Decide a vista inicial UMA vez, na 1ª leitura CONCLUSIVA do status (null = ainda checando).
  // Abriu desconectado → manda pro QR (aba Celular, onde a conexão vive). Abriu conectado → NÃO
  // redireciona em quedas posteriores: um blip transitório (status "Starting" no meio, ou um poll
  // que falhou) não pode te ARRANCAR da tela em que você está — era o bug de "iniciar disparo volta
  // pra Celular e o QR aparece" (um blip do status trocava a aba no meio do disparo). Queda real
  // mid-sessão continua visível pela UI (o botão "Sincronizar" some), sem trocar a aba à força.
  useEffect(() => {
    if (wahaWorking === null || didInitViewRef.current) {
      return;
    }
    didInitViewRef.current = true;
    if (wahaWorking === false) {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setView("phone");
    }
  }, [wahaWorking]);

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
        <nav className="tabs">
            <button
              type="button"
              className={`tab-btn${view === "phone" ? " active" : ""}`}
              onClick={() => setView("phone")}
            >
              Celular
            </button>
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
      ) : view === "collector" ? (
        <CollectorScreen />
      ) : view === "groups" ? (
        <GroupsScreen />
      ) : view === "contacts" ? (
        <ContactsScreen />
      ) : view === "campaigns" ? (
        <CampaignsScreen />
      ) : view === "phone" ? (
        <LivePhoneScreen url={EMULATOR_URL ?? ""} viewerKind={EMULATOR_KIND} udid={EMULATOR_UDID} showServerOption={PHONE_SERVER_OPTION} onDisconnect={wahaWorking === true ? () => setConfirmDisconnect(true) : undefined} onOpenConversation={openConversation} />
      ) : (
        <main className="three-col">
          <ConversationList selectedId={selected?.id ?? null} onSelect={setSelected} onStarted={setSelected} />
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
    // Reconexão com BACKOFF (em vez do retry padrão agressivo do EventSource): quando a conexão cai
    // — ex.: API reiniciou — fecha e reconecta com intervalo crescente (2s→...→30s), resetando ao
    // reconectar. Reduz o martelar no servidor e o ruído de erros de rede no console durante o boot.
    // (Os erros de rede no console são do navegador e não dá pra suprimir via JS; isto só os diminui.)
    let es: EventSource | null = null;
    let retryMs = 2000;
    let timer: number | undefined;
    let closed = false;

    const connect = () => {
      if (closed) return;
      es = new EventSource(`${apiUrl}/api/presence/connect`);
      es.onopen = () => {
        retryMs = 2000; // reconectou com sucesso → zera o backoff
      };
      es.onerror = () => {
        es?.close();
        if (closed) return;
        timer = window.setTimeout(connect, retryMs);
        retryMs = Math.min(retryMs * 2, 30000);
      };
    };
    connect();

    return () => {
      closed = true;
      if (timer) window.clearTimeout(timer);
      es?.close();
    };
  }, []);

  return (
    <AuthProvider>
      <Shell />
    </AuthProvider>
  );
}
