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

// Mínimo de dígitos plausível pra um número brasileiro: DDI (55) + DDD (2) + número (≥7).
const MIN_PHONE_DIGITS = 12;

export function WhatsAppOnboarding({ onWorking }: Props) {
  const { logout } = useAuth();
  const [status, setStatus] = useState<WahaStatus>("Unknown");
  const [error, setError] = useState<string | null>(null);
  const [qrUrl, setQrUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [autoRetries, setAutoRetries] = useState(0);
  // Já começa com o DDI do Brasil (55) — o tool é Brasil-only; o usuário completa com DDD + número.
  const [phoneInput, setPhoneInput] = useState("55");
  const [pairingCode, setPairingCode] = useState<string | null>(null);
  const [pairingBusy, setPairingBusy] = useState(false);
  const previousBlobRef = useRef<string | null>(null);
  // Só os dígitos — base única pra validar e habilitar o botão (sem repetir regex/limite na tela).
  const phoneDigits = phoneInput.replace(/\D/g, "");

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
      // Código de pareamento também perde validade ao sair do scan (ex.: pareou ou reiniciou).
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setPairingCode(null);
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
      } catch {
        // QR é best-effort: uma falha transitória (ex.: 409 quando a sessão pisca pra fora do
        // scan no ciclo do NOWEB) se auto-corrige no próximo fetch. O estado real vem do
        // pollStatus, então não poluímos a tela com erro de QR.
      }
    }
    void loadQr();
    // Recarrega o QR mais rápido que a rotação do WAHA (~20s) pra ele nunca ficar velho na tela —
    // escanear um QR expirado é a causa do "confirma e volta" sem parear.
    const handle = setInterval(loadQr, 10_000);
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

  // Conexão por CÓDIGO (alternativa ao QR, imune ao timing do scan): manda o número com DDI e
  // mostra o código pra digitar no WhatsApp. O polling de status vira "Working" quando parear.
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

            <div className="pairing-alt">
              <p className="muted tiny">
                O scan não fecha? Conecte <b>por código</b> (não depende do timing do QR):
              </p>
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
                  <p className="muted tiny">
                    No celular: <b>Aparelhos conectados → Conectar um aparelho → Conectar com número
                    de telefone</b>, e digite:
                  </p>
                  <div className="pairing-code">{pairingCode}</div>
                </div>
              )}
            </div>
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
