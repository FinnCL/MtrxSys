import { useCallback, useEffect, useRef, useState } from "react";
import { api, ApiError } from "../api/client";
import type { WahaStatus } from "../api/types";

// Painel de conexão do WhatsApp (QR + código de pareamento + status), EMBUTÍVEL na aba "Celular".
// Extraído do antigo WhatsAppOnboarding (que era uma tela cheia bloqueando o dashboard) — agora a
// conexão vive junto do aparelho virtual, sem travar o resto. `onConnected` avisa quando parear.
interface Props {
  onConnected?: () => void;
  // Esconde o QR e mostra SÓ o código de pareamento. Usado no fluxo emulador-principal: o emulador
  // NÃO tem câmera pra escanear, então o QR é inútil — só o código funciona.
  codeOnly?: boolean;
}

// Quantas vezes o QR é regenerado sozinho ao expirar (Failed) antes de pedir clique manual.
const MAX_AUTO_RETRIES = 8;
// Mínimo de dígitos plausível pra um número brasileiro: DDI (55) + DDD (2) + número (≥7).
const MIN_PHONE_DIGITS = 12;
// Máximo no input: BR COM o "9 extra" = 55 + DDD(2) + 9 + 8díg = 13. Acima disso é digitação errada.
const MAX_PHONE_DIGITS = 13;

// Remove o "9 extra" (nono dígito) de celular BR: o WhatsApp usa o número SEM ele no wid/pareamento.
// 55 + DDD(2) + 9 + 8díg (13) → 55 + DDD + 8díg (12). Só casa o padrão BR-com-9; o resto passa intacto.
function stripBrNinthDigit(digits: string): string {
  const m = /^55(\d{2})9(\d{8})$/.exec(digits);
  return m ? `55${m[1]}${m[2]}` : digits;
}

export function WhatsAppConnect({ onConnected, codeOnly }: Props) {
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
  // Número que REALMENTE vai pro WhatsApp: normalizado sem o 9 extra (o usuário pode digitar com 9).
  const pairingNumber = stripBrNinthDigit(phoneDigits);

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

  // Auto-preenche o número REAL do emulador (registration_jid) no fluxo codeOnly — evita digitar o
  // número errado (a causa do pareamento não conectar). Usa o número canônico, SEM normalizar.
  useEffect(() => {
    if (!codeOnly) return;
    let cancelled = false;
    void api.phoneWhatsAppNumber()
      .then((r) => { if (!cancelled && r.number) setPhoneInput(r.number); })
      .catch(() => {});
    return () => { cancelled = true; };
  }, [codeOnly]);

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
        // 409 = NESTE instante a sessão não está servindo QR. Com o GOWS isso é a ROTAÇÃO do QR: a
        // cada ~minuto ele reconecta e emite um QR novo, SEM mudar o status (segue SCAN_QR_CODE).
        // NÃO pode matar o poll aqui: se matar, o QR exibido CONGELA no último (expirado) e o scan
        // falha — era a causa de "escaneei e não conectou". Reconsulta o status (se realmente saiu de
        // ScanQrCode, o efeito re-roda e limpa) e deixa o intervalo tentar de novo no próximo tick.
        if (ex instanceof ApiError && ex.status === 409) {
          void pollStatus();
          return;
        }
        // Outras falhas: best-effort — o próximo tick tenta de novo (o intervalo segue vivo).
      }
    }
    void loadQr();
    // 5s (era 10s): durante a rotação do QR, refresca mais rápido pro usuário sempre ter um QR válido.
    handle = setInterval(loadQr, 5_000);
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
    // Número canônico do WhatsApp: BR sem o 9 extra (o usuário pode digitar com ou sem — normalizamos).
    const number = stripBrNinthDigit(phoneDigits);
    if (number.length < MIN_PHONE_DIGITS) {
      setError("Informe o número com DDI + DDD, sem o 9 extra. Ex.: 557133334444.");
      return;
    }
    setPairingBusy(true);
    setError(null);
    setPairingCode(null);
    try {
      // Reinicia a sessão pra o código sair FRESCO (a sessão GOWS expira ~1min) e AGUARDA (poll) ela
      // voltar pra ScanQrCode antes de pedir o código. O request-code só funciona nesse estado; o tempo
      // fixo de 6s fazia o pedido chegar cedo demais e voltar SEM código (aí nada era digitado).
      await api.wahaReset();
      let ready = false;
      for (let i = 0; i < 20; i++) {
        await new Promise((r) => setTimeout(r, 1500));
        try {
          const s = await api.wahaStatus();
          if (s.status === "ScanQrCode") { ready = true; break; }
        } catch { /* status instável no restart: ignora e tenta de novo */ }
      }
      if (!ready) {
        setError("A sessão não ficou pronta a tempo. Clique 'Gerar e digitar' de novo.");
        return;
      }
      const { code } = await api.wahaPairingCode(number);
      if (!code) {
        setError("O WhatsApp não retornou o código. Tente de novo em alguns segundos.");
        return;
      }
      setPairingCode(code);
      // Fluxo emulador (codeOnly): já DIGITA o código no emulador (campo focado) na hora — sem
      // copiar/colar, timing mínimo (o código do WhatsApp expira rápido). O usuário toca o campo
      // no emulador ANTES de gerar. Remove traço/espaço (o campo do WhatsApp já formata).
      if (codeOnly) {
        await api.phoneText(code.replace(/[\s-]/g, ""));
      }
    } catch (ex) {
      setError(ex instanceof Error ? ex.message : String(ex));
    } finally {
      setPairingBusy(false);
    }
  }, [phoneDigits, codeOnly]);

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
          <p className="muted tiny">Sessão parada. Inicie pra gerar o pareamento.</p>
          <button type="button" onClick={() => void startSession()} disabled={busy}>
            {busy ? "Iniciando..." : "Iniciar sessão"}
          </button>
        </>
      )}

      {status === "Starting" && <p className="muted tiny">Iniciando engine... aguarde alguns segundos.</p>}

      {status === "ScanQrCode" && (
        <>
          {!codeOnly && (
            <>
              <div className="qr-frame">
                {qrUrl ? <img src={qrUrl} alt="QR de pareamento" /> : <p className="muted">Carregando QR...</p>}
              </div>
              <p className="muted tiny">O QR rotaciona a cada ~20s automaticamente.</p>
            </>
          )}

          <div className="pairing-alt">
            <p className="muted tiny">
              {codeOnly ? (
                <><b>Código de pareamento:</b></>
              ) : (
                <>Não fecha o scan? Conecte <b>por código</b>:</>
              )}
            </p>
            <div className="pairing-row">
              <input
                type="tel"
                inputMode="numeric"
                placeholder="Ex.: 557133334444 (sem o 9)"
                value={phoneInput}
                onChange={(e) => setPhoneInput(e.target.value.replace(/\D/g, "").slice(0, MAX_PHONE_DIGITS))}
                maxLength={MAX_PHONE_DIGITS}
                disabled={pairingBusy}
                aria-label="Número do WhatsApp com DDI e DDD, sem o 9 extra"
              />
              <button
                type="button"
                onClick={() => void requestPairingCode()}
                disabled={pairingBusy || pairingNumber.length < MIN_PHONE_DIGITS}
              >
                {pairingBusy ? "Gerando..." : codeOnly ? "Gerar e digitar" : "Gerar código"}
              </button>
            </div>
            {pairingNumber !== phoneDigits && pairingNumber.length >= MIN_PHONE_DIGITS && (
              <p className="muted tiny">Vamos usar <b>{pairingNumber}</b> — sem o 9 extra.</p>
            )}
            {pairingCode && (
              <div className="pairing-code-box">
                <p className="muted tiny">
                  {codeOnly ? "Digite no emulador:" : <>No celular: <b>Conectar com número de telefone</b>, e digite:</>}
                </p>
                <div className="pairing-code">{pairingCode}</div>
              </div>
            )}
          </div>
        </>
      )}

      {status === "Failed" &&
        (autoRetries < MAX_AUTO_RETRIES ? (
          <p className="muted tiny">
            Conexão caiu. Reconectando
            {autoRetries > 0 ? ` (${autoRetries}/${MAX_AUTO_RETRIES})` : ""}...
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
    case "ScanQrCode": return "aguardando pareamento";
    case "Working": return "conectado";
    case "Failed": return "falha";
    default: return "desconhecido";
  }
}
