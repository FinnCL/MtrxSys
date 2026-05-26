import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";
import type { WahaStatus } from "../api/types";
import { useAuth } from "../auth/useAuth";

interface Props {
  onWorking: () => void;
}

export function WhatsAppOnboarding({ onWorking }: Props) {
  const { logout } = useAuth();
  const [status, setStatus] = useState<WahaStatus>("Unknown");
  const [error, setError] = useState<string | null>(null);
  const [qrUrl, setQrUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
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

  async function startSession() {
    setBusy(true);
    setError(null);
    try {
      await api.wahaStart();
      await pollStatus();
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setBusy(false);
    }
  }

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
          <>
            <p className="error">A sessão entrou em estado FAILED.</p>
            <button type="button" onClick={() => void startSession()} disabled={busy}>
              Tentar de novo
            </button>
          </>
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
