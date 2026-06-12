import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type { WahaStatus } from "../api/types";
import { useAuth } from "../auth/useAuth";

interface Props {
  onWorking: () => void;
}

// Quantas vezes o QR é regenerado sozinho ao expirar (Failed) antes de desistir e pedir clique
// manual. Cada ciclo mostra um QR por ~1-2min, então o teto cobre vários minutos de tentativas —
// suficiente pra quem está prestes a escanear, sem ficar gerando QR pra sempre se ninguém vai.
const MAX_AUTO_RETRIES = 8;

export function WhatsAppOnboarding({ onWorking }: Props) {
  const { logout } = useAuth();
  const [status, setStatus] = useState<WahaStatus>("Unknown");
  const [error, setError] = useState<string | null>(null);
  const [qrUrl, setQrUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [autoRetries, setAutoRetries] = useState(0);
  const previousBlobRef = useRef<string | null>(null);

  const pollStatus = useCallback(async () => {
    try {
      const resp = await api.wahaStatus();
      setStatus(resp.status);
      setError(null);
      if (resp.status === "Working") {
        onWorking();
      }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }, [onWorking]);

  useEffect(() => {
    // Polling do status do WAHA (sistema externo): setState assíncrono pós-await, não
    // cascateia — uso legítimo de efeito.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void pollStatus();
    const handle = setInterval(pollStatus, 3_000);
    return () => clearInterval(handle);
  }, [pollStatus]);

  useEffect(() => {
    if (status !== "ScanQrCode") {
      if (previousBlobRef.current) {
        URL.revokeObjectURL(previousBlobRef.current);
        previousBlobRef.current = null;
      }
      // Teardown do efeito que gerencia o blob do QR (recurso externo) ao sair do scan.
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setQrUrl(null);
      return;
    }

    let cancelled = false;
    async function loadQr() {
      try {
        const url = await api.wahaQrBlobUrl();
        if (cancelled) {
          URL.revokeObjectURL(url);
          return;
        }
        if (previousBlobRef.current) {
          URL.revokeObjectURL(previousBlobRef.current);
        }
        previousBlobRef.current = url;
        setQrUrl(url);
      } catch (ex) {
        if (!cancelled) setError(ex instanceof Error ? ex.message : String(ex));
      }
    }
    void loadQr();
    const handle = setInterval(loadQr, 18_000);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [status]);

  useEffect(
    () => () => {
      if (previousBlobRef.current) {
        URL.revokeObjectURL(previousBlobRef.current);
      }
    },
    [],
  );

  // Executa uma ação de sessão no WAHA com o mesmo ciclo: trava o botão, limpa erro, roda a ação,
  // re-lê o status e destrava — falha vira mensagem na tela. Compartilhado por start e reset.
  const runWahaAction = useCallback(async (action: () => Promise<unknown>) => {
    setBusy(true);
    setError(null);
    try {
      await action();
      await pollStatus();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setBusy(false);
    }
  }, [pollStatus]);

  // "Iniciar sessão" (Stopped): reusa a auth salva e reconecta sem QR — por isso wahaStart, jamais
  // reset (reset apagaria a credencial de um chip já pareado, forçando re-pareamento à toa).
  const startSession = useCallback(() => runWahaAction(api.wahaStart), [runWahaAction]);

  // Recupera uma sessão FAILED: reset (delete + recria) é o que gera um QR realmente novo, já que
  // aqui o pareamento falhou. Usado pelo botão manual e pelo auto-retry.
  const retrySession = useCallback(() => runWahaAction(api.wahaReset), [runWahaAction]);

  // Auto-retry do QR: ao expirar (Failed), regenera sozinho — sem clique — até MAX_AUTO_RETRIES.
  // O contador zera ao conectar (Working); passado o teto, cai no botão manual abaixo.
  useEffect(() => {
    if (status !== "Failed" || busy || autoRetries >= MAX_AUTO_RETRIES) {
      return;
    }
    const handle = setTimeout(() => {
      setAutoRetries((n) => n + 1);
      void retrySession();
    }, 1_500);
    return () => clearTimeout(handle);
  }, [status, busy, autoRetries, retrySession]);

  useEffect(() => {
    if (status === "Working" && autoRetries !== 0) {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setAutoRetries(0);
    }
  }, [status, autoRetries]);

  return (
    <div className="onboarding-shell">
      <div className="onboarding-card">
        <h1>Conectar WhatsApp</h1>
        <div className="status-row">
          <span className={`status-dot status-${status}`} />
          <span>Status: {labelFor(status)}</span>
        </div>

        {error && <p className="error">{error}</p>}

        {status === "Stopped" && (
          <>
            <p className="muted">A sessão do WhatsApp está parada. Inicie pra gerar o QR de pareamento.</p>
            <button type="button" onClick={() => void startSession()} disabled={busy}>
              {busy ? "Iniciando..." : "Iniciar sessão"}
            </button>
          </>
        )}

        {status === "Starting" && (
          <p className="muted">Iniciando engine... aguarde alguns segundos.</p>
        )}

        {status === "ScanQrCode" && (
          <>
            <p className="muted">
              Abra o WhatsApp no celular → <b>Aparelhos conectados</b> → <b>Conectar um aparelho</b> e escaneie:
            </p>
            <div className="qr-frame">
              {qrUrl ? (
                <img src={qrUrl} alt="QR de pareamento" />
              ) : (
                <p className="muted">Carregando QR...</p>
              )}
            </div>
            <p className="muted tiny">O QR rotaciona a cada ~20s automaticamente.</p>
          </>
        )}

        {status === "Failed" && (
          autoRetries < MAX_AUTO_RETRIES ? (
            <p className="muted">
              O QR expirou sem leitura. Gerando um novo automaticamente
              {autoRetries > 0 ? ` (tentativa ${autoRetries}/${MAX_AUTO_RETRIES})` : ""}...
            </p>
          ) : (
            <>
              <p className="error">Não foi possível parear após várias tentativas automáticas.</p>
              <button
                type="button"
                onClick={() => {
                  setAutoRetries(0);
                  void retrySession();
                }}
                disabled={busy}
              >
                Tentar de novo
              </button>
            </>
          )
        )}

        {status === "Unknown" && <p className="muted">Consultando status...</p>}

        <div className="onboarding-escape">
          <button type="button" className="text-link" onClick={logout}>
            Sair / trocar usuário
          </button>
        </div>
      </div>
    </div>
  );
}

function labelFor(s: WahaStatus): string {
  switch (s) {
    case "Stopped": return "parada";
    case "Starting": return "iniciando";
    case "ScanQrCode": return "aguardando scan";
    case "Working": return "conectado";
    case "Failed": return "falha";
    default: return "desconhecido";
  }
}
