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
  // A aba está à vista? Com o keep-alive da aba Celular (ver App.tsx) este componente pode continuar
  // MONTADO fora de vista: sem este gate, o poll de status (3s) e principalmente o do QR (5s, que bate
  // na WAHA) seguiriam rodando pra sempre em segundo plano. Continua montado de propósito — assim um
  // código de pareamento já gerado não some quando você dá uma passada em outra aba.
  active?: boolean;
}

// Quantas vezes o QR é regenerado sozinho ao expirar (Failed) antes de pedir clique manual.
// BAIXO de propósito (anti-ban): cada retry é um wahaReset (logout+delete+recria = NOVO vínculo de
// aparelho). Muitos re-vínculos rápidos = sinal de abuso → o WhatsApp restringe. 2 tentativas espaçadas
// e depois pede clique manual — em vez das 8 a cada 1,5s de antes, que ajudavam a queimar a conta.
const MAX_AUTO_RETRIES = 2;
// Espaço entre as auto-tentativas. Um reset completo leva segundos; re-vincular a cada 1,5s era churn.
const AUTO_RETRY_BACKOFF_MS = 15_000;
// Uma sessão sadia sai de STARTING (vira Working/ScanQrCode) em segundos. Se ficar PRESA em STARTING
// além disso, a conexão travou — visto em PROD: um envio a contato FRIO tomou 463 (tctoken) e o
// WhatsApp derrubou o socket; o engine ficou "reconnecting..." em STARTING e nunca voltou. Antes, o
// front mostrava "Iniciando engine... aguarde" pra sempre (armadilha: esperar não traz conexão).
// Passado este tempo, faz um RESTART NÃO-DESTRUTIVO (api.wahaStart = EnsureSessionStarted: mantém o
// número; NÃO é o wahaReset que re-pareia) e avisa. Poucas tentativas (anti-churn), depois pede ação.
const STARTING_STUCK_MS = 90_000;
const MAX_STARTING_RESTARTS = 2;
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

export function WhatsAppConnect({ onConnected, codeOnly, active = true }: Props) {
  const [status, setStatus] = useState<WahaStatus>("Unknown");
  const [error, setError] = useState<string | null>(null);
  const [qrUrl, setQrUrl] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const [autoRetries, setAutoRetries] = useState(0);
  const [startingRestarts, setStartingRestarts] = useState(0);
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
    if (!active) return; // fora de vista: não consulta (volta a consultar na hora em que reaparecer)
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void pollStatus();
    const handle = setInterval(pollStatus, 3_000);
    return () => clearInterval(handle);
  }, [pollStatus, active]);

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
    if (!active) {
      // Fora de vista: NÃO fica puxando QR novo da WAHA. Mantém o que já está na tela (não limpa o
      // qrUrl) — ao voltar, o efeito re-roda e busca um QR fresco na hora.
      return () => {
        cancelled = true;
      };
    }
    void loadQr();
    // 5s (era 10s): durante a rotação do QR, refresca mais rápido pro usuário sempre ter um QR válido.
    const handle = setInterval(loadQr, 5_000);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  }, [status, pollStatus, active]);

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
  // Restart MANUAL não-destrutivo (o mesmo que a recuperação automática faz): zera os contadores de
  // auto-tentativa e reinicia via wahaStart (EnsureSessionStarted — mantém o número; NÃO re-pareia).
  // Escape hatch pro operador destravar na hora sem esperar os 90s do auto.
  const manualRestart = useCallback(() => {
    setAutoRetries(0);
    setStartingRestarts(0);
    return runWahaAction(api.wahaStart);
  }, [runWahaAction]);

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

  // `!active` no gate NÃO é detalhe de performance: esta auto-tentativa chama wahaReset, que RE-VINCULA
  // o aparelho (ver MAX_AUTO_RETRIES acima — re-vínculos seguidos queimam a conta). Antes do keep-alive
  // da aba, sair da tela DESMONTAVA o componente e cancelava este timer; sem o gate, ele passaria a
  // disparar resets sozinho com o operador olhando outra aba. Manter a tela viva não pode mudar o que o
  // sistema faz por conta própria. Ao voltar pra aba o timer se rearma normalmente.
  useEffect(() => {
    if (!active || status !== "Failed" || busy || autoRetries >= MAX_AUTO_RETRIES) {
      return;
    }
    const handle = setTimeout(() => {
      setAutoRetries((n) => n + 1);
      void retrySession();
    }, AUTO_RETRY_BACKOFF_MS);
    return () => clearTimeout(handle);
  }, [status, busy, autoRetries, retrySession, active]);

  useEffect(() => {
    if (status === "Working" && autoRetries !== 0) {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setAutoRetries(0);
    }
  }, [status, autoRetries]);

  // STARTING preso → restart não-destrutivo. Não interfere num start/reset manual (busy) nem no
  // pareamento por código (pairingBusy), que já reinicia por conta própria.
  // SEM gate de `active`, ao contrário da auto-tentativa acima, e a assimetria é proposital: aqui é
  // wahaStart (EnsureSessionStarted — MANTÉM o número, não re-pareia). Destravar uma sessão presa
  // enquanto o operador trabalha em outra aba é ganho puro; o que não pode rodar desatendido é o que
  // re-vincula o aparelho.
  useEffect(() => {
    if (status !== "Starting" || busy || pairingBusy || startingRestarts >= MAX_STARTING_RESTARTS) {
      return;
    }
    const handle = setTimeout(() => {
      setStartingRestarts((n) => n + 1);
      void startSession();
    }, STARTING_STUCK_MS);
    return () => clearTimeout(handle);
  }, [status, busy, pairingBusy, startingRestarts, startSession]);

  // Zera o contador ao SAIR de STARTING pra um estado bom (conectou ou já pede QR) — libera a
  // recuperação automática de um travamento futuro. Failed tem seu próprio caminho (autoRetries).
  useEffect(() => {
    if ((status === "Working" || status === "ScanQrCode") && startingRestarts !== 0) {
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setStartingRestarts(0);
    }
  }, [status, startingRestarts]);

  return (
    <div className="wa-connect">
      <div className="status-row">
        <span className={`status-dot status-${status}`} />
        <span>Conexão: {labelFor(status)}</span>
        {/* Escape hatch manual: reinicia sem perder o número quando a conexão trava. Escondido quando
            NÃO faz sentido ou DUPLICARIA um botão do bloco abaixo: conectado (Working), parado (Stopped,
            já tem "Iniciar sessão"), Failed (o bloco tem "Tentar de novo") e STARTING esgotado (o bloco
            tem "Reiniciar sessão"). Sobra pra Starting normal / ScanQrCode / Unknown. */}
        {status !== "Working" && status !== "Stopped" && status !== "Failed"
          && !(status === "Starting" && startingRestarts >= MAX_STARTING_RESTARTS) && (
          <button
            type="button"
            className="wa-restart-btn"
            onClick={() => void manualRestart()}
            disabled={busy}
            title="Reinicia a conexão sem re-parear (mantém o número)"
          >
            {busy ? "Reiniciando..." : "Reiniciar conexão"}
          </button>
        )}
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

      {status === "Starting" &&
        (startingRestarts >= MAX_STARTING_RESTARTS ? (
          <>
            <p className="error">A conexão travou em "iniciando". Reinicie ou gere um QR novo.</p>
            <button type="button" onClick={() => { setStartingRestarts(0); void startSession(); }} disabled={busy}>
              Reiniciar sessão
            </button>
          </>
        ) : startingRestarts > 0 ? (
          <p className="muted tiny">Conexão presa — reiniciando...</p>
        ) : (
          <p className="muted tiny">Iniciando engine... aguarde alguns segundos.</p>
        ))}

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
