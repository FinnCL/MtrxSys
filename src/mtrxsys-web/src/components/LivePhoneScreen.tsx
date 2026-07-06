import { useCallback, useEffect, useState } from "react";
import { api, type ChipIdentity, type PhoneStatus } from "../api/client";
import { WhatsAppConnect } from "./WhatsAppConnect";
import { WarmupCard } from "./WarmupCard";

// Aba "Celular" = o "aparelho virtual". Dois mundos na mesma aba:
//  1) Tela do Android em container (redroid) espelhada pelo ws-scrcpy e embutida aqui
//     (`url`=VITE_EMULATOR_URL, `udid`=VITE_EMULATOR_UDID). Ver docs/phone.md.
//  2) Identidade do aparelho virtual WAHA (companion) que faz o disparo (número/nome reais, ao vivo).
//  + seção recolhível "opção de servidor" (Android em container no host Linux — redroid, sem KVM).
interface LivePhoneScreenProps {
  url: string; // tela embutível (ws-scrcpy do redroid/emulador)
  viewerKind?: string; // "scrcpy" → monta o deep-link de stream (pula a lista); senão embute a url direto
  udid?: string; // device adb a espelhar (redroid: host.docker.internal:5555; configurável por ambiente)
  showServerOption?: boolean; // mostra a seção "Android em container" — só faz sentido no servidor
  onDisconnect?: () => void; // abre a confirmação de desconectar o WhatsApp (só quando conectado)
}

// Monta o link de stream DIRETO do ws-scrcpy (pula a lista de devices). Formato extraído do source do
// ws-scrcpy: #!action=stream&udid=..&player=broadway&ws=<proxy-adb>. A porta do server no device = 8886.
function scrcpyStreamUrl(base: string, udid: string): string {
  try {
    const u = new URL(base);
    // Só http(s) vira tela embutida. Bloqueia `javascript:`/`data:` etc. que `new URL` parseia mas
    // não devem virar src de iframe (vetor de XSS quando o valor vem de env/runtime).
    if (u.protocol !== "http:" && u.protocol !== "https:") {
      return "";
    }
    const wsProto = u.protocol === "https:" ? "wss:" : "ws:";
    const eu = encodeURIComponent(udid); // redroid via adb connect = "host.docker.internal:5555" (tem ':')
    const ws = `${wsProto}//${u.host}/?action=proxy-adb&remote=tcp:8886&udid=${eu}`;
    return `${base.replace(/\/$/, "")}/#!action=stream&udid=${eu}&player=broadway&ws=${encodeURIComponent(ws)}`;
  } catch {
    return base;
  }
}

// Só aceita http(s) como src de iframe. `url`/`viewUrl` vêm de env de build e da resposta da API —
// não são 100% confiáveis de ponta a ponta; isto barra `javascript:`/`data:` antes de virar src.
function safeEmbedUrl(raw: string | null | undefined): string | null {
  if (!raw) return null;
  try {
    const u = new URL(raw, window.location.href);
    if (u.protocol !== "http:" && u.protocol !== "https:") return null;
    // noVNC (tela do servidor — sem o fragment #!action=… do ws-scrcpy): conecta sozinho e ESCALA
    // a tela remota (nativa 1440x3040) pra caber no quadro do "celular", sem scroll.
    if (!u.hash) {
      u.searchParams.set("autoconnect", "true");
      u.searchParams.set("resize", "scale");
      return u.toString();
    }
    return raw;
  } catch {
    return null;
  }
}

// Iframe de tela embutida (ws-scrcpy/noVNC) com superfície mínima: `sandbox` permite só o necessário
// pro mirror funcionar (scripts + acesso à própria origem) e `allow` concede apenas clipboard.
const PHONE_IFRAME_SANDBOX = "allow-scripts allow-same-origin allow-forms";
const PHONE_IFRAME_ALLOW = "clipboard-write";

export function LivePhoneScreen({ url, viewerKind, udid, showServerOption, onDisconnect }: LivePhoneScreenProps) {
  // ws-scrcpy do redroid/emulador: abre direto na tela do device; maquete/noVNC: embute a url como está.
  const androidUrl =
    viewerKind === "scrcpy" && url ? scrcpyStreamUrl(url, udid || "emulator-5554") : url;

  const [ident, setIdent] = useState<ChipIdentity | null>(null);
  // NÃO auto-embute a tela: quando não há chip conectado, mostramos o QR do WAHA PRIMEIRO — é o que
  // você quer 99% das vezes (parear um chip pro disparo). A tela do emulador (noVNC/scrcpy) fica no
  // botão opcional "Mostrar tela do Android". Antes o servidor (PHONE_VIEW_URL setado) abria a tela
  // sozinho e o QR ficava ESCONDIDO atrás dela — origem da confusão "não acho o QR".
  const [embed, setEmbed] = useState<string | null>(null);
  const [showServer, setShowServer] = useState(false);

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

  const connected = ident?.status === "Working";

  return (
    <section className="live-phone">
      {/* Indicador do proxy REALMENTE aplicado na sessão do chip (não o só-configurado). Verde =
          o chip sai pelo IP do proxy; cinza = sai pelo IP da máquina (sem proxy). Reusa os badges
          .phone-badge do design system (ok=verde / off=cinza) em vez de cor solta. */}
      <p className="phone-off-hint" style={{ textAlign: "center", margin: "0 0 8px" }}>
        Proxy:{" "}
        {ident?.proxy ? (
          <span className="phone-badge ok">ativo {ident.proxy}</span>
        ) : (
          <span className="phone-badge off">desligado (sai pelo IP da máquina)</span>
        )}
      </p>
      <div className="phone-device">
        <div className="phone-notch" />
        {embed ? (
          <iframe
            className="phone-stage"
            src={embed}
            title="Android real"
            sandbox={PHONE_IFRAME_SANDBOX}
            allow={PHONE_IFRAME_ALLOW}
          />
        ) : connected ? (
          <div className="phone-stage phone-off">
            <p className="phone-off-title">Aparelho virtual</p>
            <p className="phone-ident-name">{ident?.name || "WhatsApp conectado"}</p>
            <p className="phone-ident-phone">{ident?.phone ?? ""}</p>
            <span className="phone-badge ok">conectado</span>
            {onDisconnect && (
              <button type="button" className="disconnect-btn phone-disconnect" onClick={onDisconnect}>
                Desconectar WhatsApp
              </button>
            )}
          </div>
        ) : (
          // Desconectado: o QR de conexão fica DENTRO da tela do aparelho (imersivo) — você escaneia
          // como se o "celular" estivesse mostrando o QR. A tela rola internamente se precisar.
          <div className="phone-stage phone-off phone-connect-screen">
            <WhatsAppConnect onConnected={refreshIdent} />
          </div>
        )}
      </div>

      {url && (
        <div className="phone-footer">
          {embed ? (
            <button type="button" className="phone-reload" onClick={() => setEmbed(null)}>
              Desligar tela
            </button>
          ) : (
            <button type="button" className="phone-activate" onClick={() => setEmbed(safeEmbedUrl(androidUrl))}>
              Mostrar tela do Android
            </button>
          )}
        </div>
      )}

      {showServerOption && (
        <>
          <button type="button" className="phone-reload" onClick={() => setShowServer((s) => !s)}>
            {showServer ? "Ocultar" : "Configurar"} aparelho no servidor
          </button>
          {showServer && <ServerAndroidPanel />}
        </>
      )}

      {/* Aquecimento de conversa (pool). Fica AQUI de propósito: você vê as conversas aparecerem no
          WhatsApp da conta acima. O motor é o WAHA (companion), não a tela do emulador. */}
      <WarmupCard />
    </section>
  );
}

// Opção de servidor: Android REAL em container (redroid, sem KVM). A aba provisiona/liga/instala tudo
// pela API (docker.sock), sem prompt. Só funciona num host Linux com Docker — fora disso, mostra
// "indisponível" de forma limpa. Aqui o Android pode virar o dispositivo PRINCIPAL (registro por SMS).
function ServerAndroidPanel() {
  const [status, setStatus] = useState<PhoneStatus | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [output, setOutput] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.phoneStatus());
    } catch {
      setStatus({ state: "unavailable", running: false, viewUrl: null });
    }
  }, []);

  useEffect(() => {
    void refresh();
    const id = setInterval(() => void refresh(), 4000);
    return () => clearInterval(id);
  }, [refresh]);

  const state = status?.state ?? "...";
  const running = status?.running ?? false;

  const run = async (name: string, fn: () => Promise<unknown>) => {
    setBusy(name);
    setErr(null);
    try {
      await fn();
    } catch (e) {
      setErr(e instanceof Error ? e.message : String(e));
    } finally {
      setBusy(null);
      await refresh();
    }
  };

  const provision = () => run("provision", async () => setStatus(await api.phoneProvision()));
  const start = () => run("start", async () => setStatus(await api.phoneStart()));
  const installWa = () =>
    run("install", async () => setOutput((await api.phoneInstallWhatsApp()).output || "(ok)"));

  return (
    <div className="phone-server">
      <p className="phone-off-hint">
        Emulador Android como <b>principal</b> do número. Dispensa o físico. A tela fica no quadro acima.
      </p>

      {running ? (
        <>
          <div className="phone-footer">
            <button type="button" className="phone-reload" onClick={() => void installWa()} disabled={busy !== null}>
              {busy === "install" ? "Instalando…" : "Instalar WhatsApp"}
            </button>
          </div>
          <p className="phone-off-hint">
            <b>1)</b> Instalar · <b>2)</b> Registrar (SMS) · <b>3)</b> Vincular o WAHA
          </p>
        </>
      ) : state === "not_created" ? (
        <div className="phone-footer">
          <button type="button" className="phone-activate" onClick={() => void provision()} disabled={busy !== null}>
            {busy === "provision" ? "Ligando…" : "Ligar o Android"}
          </button>
        </div>
      ) : state === "exited" || state === "created" ? (
        <div className="phone-footer">
          <button type="button" className="phone-activate" onClick={() => void start()} disabled={busy !== null}>
            {busy === "start" ? "Ligando…" : "Ligar o Android"}
          </button>
        </div>
      ) : (
        <p className="phone-off-hint">Indisponível neste host (sem Docker). Rode num servidor Linux.</p>
      )}

      {err && (
        <p className="phone-off-hint" style={{ color: "var(--danger)" }}>{err}</p>
      )}

      {output !== null && (
        <div className="phone-logs">
          <div className="phone-logs-head">
            <span>saída</span>
            <button type="button" className="phone-reload" onClick={() => setOutput(null)}>fechar</button>
          </div>
          <pre>{output}</pre>
        </div>
      )}
    </div>
  );
}
