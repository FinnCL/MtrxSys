import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError } from "../api/client";
import type { WahaStatus } from "../api/types";

// Painel de conexão do WhatsApp (QR + código de pareamento + status), EMBUTÍVEL na aba "Celular".
// Extraído do antigo WhatsAppOnboarding (que era uma tela cheia bloqueando o dashboard) — agora a
// conexão vive junto do aparelho virtual, sem travar o resto. `onConnected` avisa quando parear.
interface Props {
  onConnected?: () => void;
}

// Quantas vezes o QR é regenerado sozinho ao expirar (Failed) antes de pedir clique manual.
const MAX_AUTO_RETRIES = 8;
// Mínimo de dígitos plausível pra um número brasileiro: DDI (55) + DDD (2) + número (≥7).
const MIN_PHONE_DIGITS = 12;

export function WhatsAppConnect({ onConnected }: Props) {
  const [status, setStatus] = useState<WahaStatus>("Unknown");
  const [error, setError] = useState<string | null>(null);
  const [qrUrl, setQrUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [autoRetries, setAutoRetries] = useState(0);
  const [phoneInput, setPhoneInput] = useState("55");
  const [pairingCode, setPairingCode] = useState<string | null>(null);
  const [pairingBusy, setPairingBusy] = useState(false);
  const previousBlobRef = useRef<string | null>(null);
  const phoneDigits = phoneInput.replace(/\D/g, "");

  const pollStatus = useCallback(async () => {
    try {
      const resp = await api.wahaStatus();
      setStatus(resp.status);
      setError(null);
      if (resp.status === "Working") {
        onConnected?.();
      }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    }
  }, [onConnected]);

  useEffect(() => {
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
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setQrUrl(null);
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setPairingCode(null);
      return;
    }
    let cancelled = false;
    let handle: ReturnType<typeof setInterval> | undefined;
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
        // 409 = a sessão NÃO está mais em ScanQrCode (nosso `status` está defasado). Insistir a
        // cada 10s só floodaria o console com 409. Para o poll e reavalia o status — o efeito
        // reinicia sozinho se a sessão voltar a ScanQrCode. Outras falhas: best-effort, tenta de novo.
        if (ex instanceof ApiError && ex.status === 409) {
          if (handle) clearInterval(handle);
          void pollStatus();
        }
      }
    }
    void loadQr();
    handle = setInterval(loadQr, 10_000);
    return () => {
      cancelled = true;
      if (handle) clearInterval(handle);
    };
  }, [status, pollStatus]);

  useEffect(
    () => () => {
      if (previousBlobRef.current) {
        URL.revokeObjectURL(previousBlobRef.current);
      }
    },
    [],
  );

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

  const startSession = useCallback(() => runWahaAction(api.wahaStart), [runWahaAction]);
  const retrySession = useCallback(() => runWahaAction(api.wahaReset), [runWahaAction]);

  const requestPairingCode = useCallback(async () => {
    if (phoneDigits.length < MIN_PHONE_DIGITS) {
      setError("Informe o número com DDI+DDD, ex.: 5571999998888.");
      return;
    }
    setPairingBusy(true);
    setError(null);
    setPairingCode(null);
    try {
      const { code } = await api.wahaPairingCode(phoneDigits);
      if (!code) {
        setError("O WhatsApp não retornou o código. Tente de novo em alguns segundos.");
        return;
      }
      setPairingCode(code);
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setPairingBusy(false);
    }
  }, [phoneDigits]);

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
    <div className="wa-connect">
      <div className="status-row">
        <span className={`status-dot status-${status}`} />
        <span>Conexão: {labelFor(status)}</span>
      </div>

      {error && <p className="error">{error}</p>}

      {status === "Stopped" && (
        <>
          <p className="muted tiny">A sessão está parada. Inicie pra gerar o QR de pareamento.</p>
          <button type="button" onClick={() => void startSession()} disabled={busy}>
            {busy ? "Iniciando..." : "Iniciar sessão"}
          </button>
        </>
      )}

      {status === "Starting" && <p className="muted tiny">Iniciando engine... aguarde alguns segundos.</p>}

      {status === "ScanQrCode" && (
        <>
          <div className="qr-frame">
            {qrUrl ? <img src={qrUrl} alt="QR de pareamento" /> : <p className="muted">Carregando QR...</p>}
          </div>
          <p className="muted tiny">O QR rotaciona a cada ~20s automaticamente.</p>

          <div className="pairing-alt">
            <p className="muted tiny">Não fecha o scan? Conecte <b>por código</b>:</p>
            <div className="pairing-row">
              <input
                type="tel"
                inputMode="numeric"
                placeholder="Ex.: 5571999998888"
                value={phoneInput}
                onChange={(e) => setPhoneInput(e.target.value)}
                disabled={pairingBusy}
                aria-label="Número do WhatsApp com DDI e DDD"
              />
              <button
                type="button"
                onClick={() => void requestPairingCode()}
                disabled={pairingBusy || phoneDigits.length < MIN_PHONE_DIGITS}
              >
                {pairingBusy ? "Gerando..." : "Gerar código"}
              </button>
            </div>
            {pairingCode && (
              <div className="pairing-code-box">
                <p className="muted tiny">No celular: <b>Conectar com número de telefone</b>, e digite:</p>
                <div className="pairing-code">{pairingCode}</div>
              </div>
            )}
          </div>
        </>
      )}

      {status === "Failed" &&
        (autoRetries < MAX_AUTO_RETRIES ? (
          <p className="muted tiny">
            O QR expirou sem leitura. Gerando um novo automaticamente
            {autoRetries > 0 ? ` (tentativa ${autoRetries}/${MAX_AUTO_RETRIES})` : ""}...
          </p>
        ) : (
          <>
            <p className="error">Não foi possível parear após várias tentativas.</p>
            <button type="button" onClick={() => { setAutoRetries(0); void retrySession(); }} disabled={busy}>
              Tentar de novo
            </button>
          </>
        ))}

      {status === "Unknown" && <p className="muted tiny">Consultando status...</p>}
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
