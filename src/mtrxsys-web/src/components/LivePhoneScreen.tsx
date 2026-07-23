import { useCallback, useState } from "react";
import { api, type ChipIdentity, type PhoneMode, type PhoneStatus } from "../api/client";
import { usePoll } from "../hooks/usePoll";
import { WhatsAppConnect } from "./WhatsAppConnect";
import { WarmupCard } from "./WarmupCard";
import { HumanPhaseCard } from "./HumanPhaseCard";

// Aba "Celular" — dois mundos, alternados pelo toggle de modo (PERSISTIDO no banco):
//  • "Sem emulador" (WahaOnly): conexão do chip por QR / identidade (WAHA + aparelho real físico).
//  • "Com emulador" (Emulator): a TELA do Android (noVNC do docker-android) embutida direto na página,
//    sem moldura — o emulador é o PRIMÁRIO e o disparo a frio sai por ele (mata o 463). Barra de navegação
//    Android + ligar/recarregar a tela. Só aparece onde há viewer configurado (VITE_EMULATOR_URL).
// O backend (endpoints, orquestrador, /api/phone/mode) segue intacto; isto é só a camada de tela.
interface LivePhoneScreenProps {
  url: string; // viewer do emulador (ws-scrcpy/noVNC). Vazio = host sem emulador → sempre WAHA+físico.
  viewerKind?: string; // usado no passo do emulador (deep-link scrcpy) — ainda não neste baseline.
  udid?: string; // idem — device adb a espelhar.
  showServerOption?: boolean; // idem — setup do Android no servidor.
  onDisconnect?: () => void; // abre a confirmação de desconectar o WhatsApp (só quando conectado).
  onOpenConversation?: (id: string) => void; // atalho da Fase Humana: leva à conversa na aba Chat.
}

export function LivePhoneScreen({ url, onDisconnect, onOpenConversation }: LivePhoneScreenProps) {
  const [ident, setIdent] = useState<ChipIdentity | null>(null);
  // Acordeão do "Conectar WhatsApp": o QR só MONTA ao abrir. Enquanto fechado, o WhatsAppConnect nem
  // existe → sem o poll de status/QR (perf). A detecção de conexão não depende dele: o refreshIdent
  // abaixo (poll leve de /api/presence/chip) vira `connected` sozinho.
  const [showConnect, setShowConnect] = useState(false);
  // A Fase Humana está segurando o disparo? Vem do HumanPhaseCard (que já faz o poll) em vez de um
  // fetch próprio — uma fonte só, sem segundo timer.
  const [humanPhaseBlocking, setHumanPhaseBlocking] = useState(false);
  // Modo PERSISTIDO da aba (fonte da verdade — vem do banco via /api/phone/mode). null = carregando.
  const [mode, setMode] = useState<PhoneMode | null>(null);
  const [modeBusy, setModeBusy] = useState(false);
  // Saúde de entrega (sensor anti-shadow-restriction) — dos envios das últimas 24h, quantos entregaram.
  const [delivery, setDelivery] = useState<{ sent: number; delivered: number; rate: number | null } | null>(null);
  // Status do container do emulador (running/booted) — só no modo emulador, pra saber se a tela sobe.
  const [phoneStatus, setPhoneStatus] = useState<PhoneStatus | null>(null);
  // Recarrega o iframe da tela (botão "recarregar") sem F5 na página inteira.
  const [frameKey, setFrameKey] = useState(0);

  const refreshIdent = useCallback(async () => {
    try {
      setIdent(await api.phoneIdentity());
    } catch {
      setIdent(null);
    }
  }, []);

  usePoll(refreshIdent, 5000);

  // Lê o modo persistido e mantém em sincronia. Só faz sentido onde há emulador disponível (url);
  // sem url a aba é sempre WAHA + físico (não há o que alternar). Falha silenciosa preserva o valor.
  usePoll(async () => {
    try {
      setMode((await api.phoneMode()).mode);
    } catch {
      /* mantém o valor atual */
    }
  }, 6000, !!url);

  const connected = ident?.status === "Working";

  // Troca o modo (o toggle único). Persiste no banco e reconcilia o container do emulador ("Emulator"
  // liga, "WahaOnly" desliga). NÃO trava mais com o chip conectado: no Caminho A o emulador é o PRIMÁRIO
  // e o WAHA é o COMPANION do MESMO número — coexistem, não são mutuamente exclusivos. Trocar de modo é
  // livre (só bloqueia durante a própria troca, modeBusy). Alternar Emulator↔WahaOnly liga/desliga o
  // container do emulador; a sessão WAHA segue de pé.
  const selectMode = async (next: PhoneMode) => {
    if (modeBusy || mode === next) return;
    setModeBusy(true);
    try {
      await api.phoneSetMode(next);
      setMode(next);
      if (url) {
        if (next === "Emulator") await api.phoneStart();
        else await api.phoneStop();
      }
    } catch {
      /* fail-safe: não quebra a aba se o docker/endpoint não responder */
    } finally {
      setModeBusy(false);
    }
  };

  const emulatorMode = mode === "Emulator" && !!url;
  // Toggle só bloqueia durante a própria troca (modeBusy). Antes travava com o chip conectado; no
  // Caminho A emulador+WAHA coexistem, então trocar é livre.
  const modeLocked = modeBusy;

  // Saúde de entrega — só quando conectado (é o chip que dispara). Poll leve; falha silenciosa.
  usePoll(async () => {
    try {
      setDelivery(await api.deliveryHealth());
    } catch {
      /* ignora */
    }
  }, 30000, connected);

  // Status do container do emulador — só no modo emulador. Diz se a tela (noVNC) tem o que embutir:
  // running=false → mostra "Ligar emulador"; unavailable → host sem docker/emulador. Poll leve.
  usePoll(async () => {
    try {
      setPhoneStatus(await api.phoneStatus());
    } catch {
      /* ignora — a tela otimista embute mesmo assim */
    }
  }, 8000, emulatorMode);

  return (
    <section className="live-phone">
      {/* Proxy REALMENTE aplicado na sessão WAHA do chip (verde) ou saída pelo IP da máquina (cinza). */}
      <p className="phone-off-hint" style={{ textAlign: "center", margin: "0 0 8px" }}>
        Proxy:{" "}
        {ident?.proxy ? (
          <span className="phone-badge ok">ativo {ident.proxy}</span>
        ) : (
          <span className="phone-badge off">desligado (sai pelo IP da máquina)</span>
        )}
      </p>

      {/* TOGGLE ÚNICO de modo (persistido no banco). Só aparece onde há emulador disponível (url). */}
      {url && mode !== null && (
        <div className="phone-mode-wrap">
          <div className="phone-mode" role="group" aria-label="Modo de disparo" aria-busy={modeBusy}>
            <button
              type="button"
              className={`phone-mode-opt${mode === "WahaOnly" ? " active" : ""}`}
              aria-pressed={mode === "WahaOnly"}
              disabled={modeLocked}
              onClick={() => void selectMode("WahaOnly")}
            >
              Sem emulador
            </button>
            <button
              type="button"
              className={`phone-mode-opt${mode === "Emulator" ? " active" : ""}`}
              aria-pressed={mode === "Emulator"}
              disabled={modeLocked}
              onClick={() => void selectMode("Emulator")}
            >
              Com emulador
            </button>
          </div>
          <p className="phone-off-hint phone-mode-hint">
            {modeBusy
              ? "Alternando…"
              : mode === "Emulator"
                ? "Disparo pela UI do emulador (primário) + WAHA companion"
                : "WAHA + aparelho real físico"}
          </p>
        </div>
      )}

      {/* Fluxo WAHA + aparelho físico. Conectado → estado PRONTO (confirma + aponta o próximo passo).
          Desconectado → a etapa "Conectar" em acordeão: desliza e monta o QR só ao clicar. */}
      {connected ? (
        <div className="phone-ident-card">
          <span className="phone-badge ok">conectado</span>
          <p className="phone-ident-name">{ident?.name || "WhatsApp conectado"}</p>
          <p className="phone-ident-phone">{ident?.phone ?? ""}</p>
          {/* Durante a Fase Humana o disparo está TRAVADO — dizer "pronto para disparar" aqui faria
              o operador ir à aba Disparo, mandar, e não ver nada sair. O card logo abaixo explica. */}
          <p className="phone-off-hint" style={{ margin: "2px 0 0" }}>
            {ident?.proxy ? "Proxy ativo. " : ""}
            {humanPhaseBlocking
              ? "Em fase de aquecimento humano — o disparo abre quando a fase fechar (veja abaixo)."
              : <>Pronto para disparar. Vá para a aba <b>Disparo</b>.</>}
          </p>
          {/* Sensor de entrega: só aparece quando JÁ HOUVE entrega confirmada (delivered > 0) — assim
              não mostra "0%" enganoso numa sessão que ainda não assina message.ack (pareada antes do
              sensor). Com ACKs fluindo, uma taxa que cai é sinal de possível restrição. */}
          {delivery && delivery.delivered > 0 && (
            <p className="phone-off-hint" style={{ margin: "2px 0 0" }}>
              Entregas 24h: <b>{delivery.delivered}/{delivery.sent}</b>
              {delivery.rate != null ? ` (${Math.round(delivery.rate * 100)}%)` : ""}
            </p>
          )}
          {onDisconnect && (
            <button type="button" className="disconnect-btn phone-disconnect" onClick={onDisconnect}>
              Desconectar WhatsApp
            </button>
          )}
        </div>
      ) : (
        <div className="phone-steps">
          <button
            type="button"
            className="phone-step-toggle"
            aria-expanded={showConnect}
            onClick={() => setShowConnect((v) => !v)}
          >
            <span>Conectar o WhatsApp</span>
            <span className="phone-step-caret">{showConnect ? "▲" : "▼"}</span>
          </button>
          {showConnect && (
            <div className="phone-step-body">
              <p className="phone-off-hint" style={{ margin: "0 0 8px" }}>
                Em <b>Aparelhos conectados</b>, toque <b>Conectar um aparelho</b> e escaneie o QR.
              </p>
              <WhatsAppConnect onConnected={refreshIdent} />
            </div>
          )}
        </div>
      )}

      {/* FASE HUMANA (dias 1-3 do chip novo). Fora do `emulatorMode` de propósito, ao contrário do
          WarmupCard: a fase é do CHIP, não do emulador, e vale nos dois modos. Só faz sentido com
          chip conectado — sem chip não há fase. O card some sozinho quando não se aplica. */}
      {connected && (
        <HumanPhaseCard onOpenConversation={onOpenConversation} onBlockingChange={setHumanPhaseBlocking} />
      )}

      {/* Modo "Com emulador": a TELA do Android (noVNC do docker-android) embutida direto, sem moldura.
          O disparo a frio sai por ESTE aparelho (o primário) — é o que mata o 463. Barra de navegação
          Android (adb keyevent) + recarregar/abrir a tela. Desligado → botão pra ligar o container. */}
      {emulatorMode && (
        <>
          {phoneStatus && !phoneStatus.running ? (
            <div className="phone-steps">
              <p className="phone-off-hint" style={{ textAlign: "center" }}>
                {phoneStatus.state === "unavailable"
                  ? "Emulador indisponível neste host (sem docker/KVM)."
                  : "O emulador está desligado. Ligue para ver a tela e disparar por ele."}
              </p>
              {phoneStatus.state !== "unavailable" && (
                <button type="button" className="phone-activate" onClick={() => void api.phoneStart()}>
                  Ligar emulador
                </button>
              )}
            </div>
          ) : (
            <>
              {/* Sem moldura de celular: só a tela. O wrapper existe apenas pra manter o aspect-ratio
                  do display e o recorte (overflow) do noVNC — nada de bezel/notch desenhado. */}
              <div className="phone-screen">
                <iframe
                  key={frameKey}
                  className="phone-stage"
                  src={url}
                  title="Tela do emulador Android (WhatsApp primário)"
                  allow="clipboard-read; clipboard-write"
                />
              </div>
              {/* Navegação do Android (◁ ○ ▢) via adb keyevent — pra operar a tela quando o mouse do
                  noVNC não basta (ex.: voltar de um menu). */}
              <div className="phone-navbar">
                {/* aria-label além do title: sem ele o leitor de tela anuncia só o glifo ("◁"). */}
                <button type="button" className="phone-nav-btn" title="Voltar" aria-label="Voltar" onClick={() => void api.phoneKey("back")}>◁</button>
                <button type="button" className="phone-nav-btn" title="Início" aria-label="Início" onClick={() => void api.phoneKey("home")}>○</button>
                <button type="button" className="phone-nav-btn" title="Recentes" aria-label="Recentes" onClick={() => void api.phoneKey("recents")}>▢</button>
              </div>
              <p className="phone-off-hint" style={{ textAlign: "center", maxWidth: 390 }}>
                Tela do Android (emulador-primário). O disparo a frio sai por aqui — sem 463.{" "}
                <button type="button" className="phone-reload" onClick={() => setFrameKey((k) => k + 1)}>
                  Recarregar tela
                </button>{" "}
                <a href={url} target="_blank" rel="noreferrer" style={{ color: "var(--accent, #00a884)" }}>
                  abrir em nova aba
                </a>
              </p>
            </>
          )}

          {/* Aquecimento de conversa (pool) — só no modo Com emulador; fora da área WAHA + físico. */}
          <WarmupCard />
        </>
      )}
    </section>
  );
}
