import { useCallback, useEffect, useState } from "react";
import { api, type ChipIdentity, type PhoneMode } from "../api/client";
import { WhatsAppConnect } from "./WhatsAppConnect";
import { WarmupCard } from "./WarmupCard";

// Aba "Celular" — RECONSTRUÍDA DO ZERO, passo a passo.
// Baseline (passo 1): o toggle de modo (PERSISTIDO no banco) + o mundo "WAHA + aparelho real físico"
// (conexão do chip por QR / identidade). O mundo "Com emulador" (tela do Android, pareamento pelo
// emulador, setup do container) será reconstruído nos próximos passos — aqui ele é só um placeholder.
// O backend (endpoints, orquestrador, /api/phone/mode) segue intacto; isto é só a camada de tela.
interface LivePhoneScreenProps {
  url: string; // viewer do emulador (ws-scrcpy/noVNC). Vazio = host sem emulador → sempre WAHA+físico.
  viewerKind?: string; // usado no passo do emulador (deep-link scrcpy) — ainda não neste baseline.
  udid?: string; // idem — device adb a espelhar.
  showServerOption?: boolean; // idem — setup do Android no servidor.
  onDisconnect?: () => void; // abre a confirmação de desconectar o WhatsApp (só quando conectado).
}

export function LivePhoneScreen({ url, onDisconnect }: LivePhoneScreenProps) {
  const [ident, setIdent] = useState<ChipIdentity | null>(null);
  // Acordeão do "Conectar WhatsApp": o QR só MONTA ao abrir. Enquanto fechado, o WhatsAppConnect nem
  // existe → sem o poll de status/QR (perf). A detecção de conexão não depende dele: o refreshIdent
  // abaixo (poll leve de /api/presence/chip) vira `connected` sozinho.
  const [showConnect, setShowConnect] = useState(false);
  // Modo PERSISTIDO da aba (fonte da verdade — vem do banco via /api/phone/mode). null = carregando.
  const [mode, setMode] = useState<PhoneMode | null>(null);
  const [modeBusy, setModeBusy] = useState(false);

  const refreshIdent = useCallback(async () => {
    try {
      setIdent(await api.phoneIdentity());
    } catch {
      setIdent(null);
    }
  }, []);

  useEffect(() => {
    void refreshIdent();
    const id = setInterval(() => void refreshIdent(), 5000);
    return () => clearInterval(id);
  }, [refreshIdent]);

  // Lê o modo persistido e mantém em sincronia. Só faz sentido onde há emulador disponível (url);
  // sem url a aba é sempre WAHA + físico (não há o que alternar). Falha silenciosa preserva o valor.
  useEffect(() => {
    if (!url) return;
    let alive = true;
    const tick = async () => {
      try {
        const m = (await api.phoneMode()).mode;
        if (alive) setMode(m);
      } catch {
        /* mantém o valor atual */
      }
    };
    void tick();
    const id = setInterval(() => void tick(), 6000);
    return () => {
      alive = false;
      clearInterval(id);
    };
  }, [url]);

  const connected = ident?.status === "Working";

  // Troca o modo (o toggle único). Persiste no banco e reconcilia o container do emulador ("Emulator"
  // liga, "WahaOnly" desliga). TRAVA de EXCLUSÃO MÚTUA: enquanto houver chip CONECTADO, não deixa
  // trocar — WAHA+físico e emulador+WAHA são mutuamente exclusivos por ambiente (a sessão WAHA vincula
  // a um aparelho só). Pra alternar, desconecte antes.
  const selectMode = async (next: PhoneMode) => {
    if (modeBusy || connected || mode === next) return;
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
  // Toggle travado quando conectado (não pode usar os dois modos ao mesmo tempo) ou durante a troca.
  const modeLocked = connected || modeBusy;

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
          <div className={`phone-mode${connected ? " locked" : ""}`} role="group" aria-label="Modo de disparo" aria-busy={modeBusy}>
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
            {connected
              ? "Conectado, desconecte para trocar de modo"
              : modeBusy
                ? "Alternando…"
                : mode === "Emulator"
                  ? "Disparo pelo emulador + WAHA"
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
          <p className="phone-off-hint" style={{ margin: "2px 0 0" }}>
            {ident?.proxy ? "Proxy ativo. " : ""}Pronto para disparar. Vá para a aba <b>Disparo</b>.
          </p>
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
                No WhatsApp do <b>celular físico</b>: Configurações → <b>Aparelhos conectados</b> →
                Conectar um aparelho, e escaneie o QR abaixo.
              </p>
              <WhatsAppConnect onConnected={refreshIdent} />
            </div>
          )}
        </div>
      )}

      {/* Placeholder honesto: o mundo "Com emulador" ainda não foi reconstruído neste baseline. */}
      {emulatorMode && (
        <p className="phone-off-hint" style={{ textAlign: "center", maxWidth: 390 }}>
          🚧 Modo <b>Com emulador</b> em construção — a tela do Android e o pareamento pelo emulador
          voltam no próximo passo. O disparo já funciona pelo WAHA.
        </p>
      )}

      {/* Aquecimento de conversa (pool). Motor é o WAHA (companion) — vale nos dois modos. */}
      <WarmupCard />
    </section>
  );
}
