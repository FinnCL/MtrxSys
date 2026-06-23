import { useCallback, useEffect, useState } from "react";
import { api, type ChipIdentity, type PhoneStatus } from "../api/client";
import { WhatsAppConnect } from "./WhatsAppConnect";
import { STEP_ICON, useProvisionFlow } from "../hooks/useProvisionFlow";

// Aba "Celular" = o "aparelho virtual". Dois mundos na mesma aba:
//  1) Tela do Android REAL (local): LDPlayer (ou emulador) no host com Play Store, espelhado pelo
//     ws-scrcpy e embutido aqui (`url`=VITE_EMULATOR_URL, `udid`=VITE_EMULATOR_UDID). Ver docs/ldplayer.md.
//  2) Identidade do aparelho virtual WAHA (companion) que faz o disparo — número/nome reais, ao vivo.
//  + seção recolhível "opção de servidor" (docker-android, só host Linux com KVM).
interface LivePhoneScreenProps {
  url: string; // tela embutível (ws-scrcpy do LDPlayer/emulador local)
  viewerKind?: string; // "scrcpy" → monta o deep-link de stream (pula a lista); senão embute a url direto
  udid?: string; // device adb a espelhar (LDPlayer: emulator-5554 / 5556…; configurável por ambiente)
  showServerOption?: boolean; // mostra a seção "Android em container (KVM)" — só faz sentido no servidor
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
    const eu = encodeURIComponent(udid); // LDPlayer via adb connect = "127.0.0.1:5555" (tem ':')
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
    return u.protocol === "http:" || u.protocol === "https:" ? raw : null;
  } catch {
    return null;
  }
}

// Iframe de tela embutida (ws-scrcpy/noVNC) com superfície mínima: `sandbox` permite só o necessário
// pro mirror funcionar (scripts + acesso à própria origem) e `allow` concede apenas clipboard.
const PHONE_IFRAME_SANDBOX = "allow-scripts allow-same-origin allow-forms";
const PHONE_IFRAME_ALLOW = "clipboard-write";

export function LivePhoneScreen({ url, viewerKind, udid, showServerOption, onDisconnect }: LivePhoneScreenProps) {
  // ws-scrcpy do LDPlayer/emulador: abre direto na tela do device; maquete/noVNC: embute a url como está.
  const androidUrl =
    viewerKind === "scrcpy" && url ? scrcpyStreamUrl(url, udid || "emulator-5554") : url;

  const [ident, setIdent] = useState<ChipIdentity | null>(null);
  // Servidor (PHONE_VIEW_URL setado): embute o Android AUTOMATICAMENTE — abre sozinho, sem clique e sem
  // cair no QR quando desconectado (é desconectado que você precisa ver a tela pra instalar/registrar).
  // Local (url vazia): começa null → mostra o QR/identidade. "Desligar tela" zera; o usuário reabre.
  const [embed, setEmbed] = useState<string | null>(safeEmbedUrl(androidUrl));
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
            <p className="phone-ident-phone">{ident?.phone ?? "—"}</p>
            <span className="phone-badge ok">conectado</span>
            {url && (
              <button type="button" className="phone-activate" onClick={() => setEmbed(safeEmbedUrl(androidUrl))}>
                Mostrar tela do Android
              </button>
            )}
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

      {embed && (
        <p className="phone-off-hint phone-explain">
          A tela abre <b>direto no Android</b>. Se ficar preta, dê alguns segundos (o emulador acorda)
          ou use <b>Abrir em nova aba</b>. Depois: <b>Play Store</b> → instalar o <b>WhatsApp</b>.
        </p>
      )}

      <div className="phone-footer">
        {embed && (
          <button type="button" className="phone-reload" onClick={() => setEmbed(null)}>
            Desligar tela
          </button>
        )}
        {embed && (
          <a className="phone-reload" href={embed} target="_blank" rel="noreferrer">
            Abrir em nova aba
          </a>
        )}
      </div>

      {showServerOption && (
        <>
          <button type="button" className="phone-reload" onClick={() => setShowServer((s) => !s)}>
            {showServer ? "Ocultar" : "Mostrar"} opção de servidor — Android em container (KVM)
          </button>
          {showServer && <ServerAndroidPanel url={url} />}
        </>
      )}
    </section>
  );
}

// Opção de servidor: Android REAL em container (docker-android). A aba provisiona/liga/instala tudo
// pela API (docker.sock), sem prompt. Só funciona num host Linux com /dev/kvm — fora disso, mostra
// "indisponível" de forma limpa. Aqui o Android pode virar o dispositivo PRINCIPAL (registro por SMS).
function ServerAndroidPanel({ url }: { url: string }) {
  const [status, setStatus] = useState<PhoneStatus | null>(null);
  const [embed, setEmbed] = useState<string | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const [logs, setLogs] = useState<string | null>(null);
  const [output, setOutput] = useState<string | null>(null);
  const [proxy, setProxy] = useState("");
  const [err, setErr] = useState<string | null>(null);

  const refresh = useCallback(async () => {
    try {
      setStatus(await api.phoneStatus());
    } catch {
      setStatus({ state: "unavailable", running: false, viewUrl: null });
    }
  }, []);

  // Orquestração do "Provisionar número" (boot→instalar→proxy→SMS→WAHA) vive no hook — o painel só
  // renderiza o checklist e dispara as ações.
  const { steps, linkQr, provBusy, error: provError, provisionNumber, confirmSms, confirmWaha } =
    useProvisionFlow(proxy, refresh);

  useEffect(() => {
    void refresh();
    const id = setInterval(() => void refresh(), 4000);
    return () => clearInterval(id);
  }, [refresh]);

  const viewUrl = status?.viewUrl ?? url ?? "";
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
  const stop = () =>
    run("stop", async () => {
      await api.phoneStop();
      setEmbed(null);
    });
  const installWa = () =>
    run("install", async () => setOutput((await api.phoneInstallWhatsApp()).output || "(ok)"));
  const applyProxy = () =>
    run("proxy", async () => setOutput((await api.phoneSetProxy(proxy)).output || "(ok)"));

  const loadLogs = async () => {
    try {
      setLogs((await api.phoneLogs(200)).logs || "(sem logs)");
    } catch (e) {
      setLogs(e instanceof Error ? e.message : String(e));
    }
  };

  return (
    <div className="phone-server">
      <p className="phone-off-hint">
        Android real em container — aqui o aparelho vira o <b>principal</b> do número (registro por
        SMS), permitindo dispensar o físico. Exige host Linux com <b>/dev/kvm</b>.
      </p>

      <div className="phone-footer">
        <button
          type="button"
          className="phone-activate"
          onClick={() => void provisionNumber()}
          disabled={provBusy}
        >
          {provBusy ? "Provisionando…" : "Provisionar número (automático)"}
        </button>
        <input
          className="phone-proxy-input"
          placeholder="proxy IP:porta (opcional)"
          value={proxy}
          onChange={(e) => setProxy(e.target.value)}
        />
      </div>

      {steps && (
        <div className="prov-steps">
          {steps.map((s) => (
            <div key={s.key} className={`prov-step prov-${s.state}`}>
              <span className="prov-dot">{STEP_ICON[s.state]}</span>
              <div className="prov-body">
                <span className="prov-label">{s.label}</span>
                {s.detail && <span className="prov-detail">{s.detail}</span>}
                {s.key === "sms" && s.state === "wait" && (
                  <button type="button" className="phone-reload prov-cta" onClick={() => void confirmSms()}>
                    Registrei o número
                  </button>
                )}
                {s.key === "waha" && s.state === "wait" && linkQr && (
                  <>
                    <img className="prov-link-qr" src={linkQr} alt="QR do WAHA pra vincular no emulador" />
                    <button type="button" className="phone-reload prov-cta" onClick={() => confirmWaha()}>
                      Vinculei o WAHA
                    </button>
                  </>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {embed ? (
        <div className="phone-device">
          <div className="phone-notch" />
          <iframe className="phone-stage" src={embed} title="Android real (servidor)" sandbox={PHONE_IFRAME_SANDBOX} allow={PHONE_IFRAME_ALLOW} />
        </div>
      ) : (
        <p className="phone-off-hint">
          estado: <b>{state}</b>
        </p>
      )}

      <div className="phone-footer">
        {running ? (
          <button type="button" className="phone-activate" onClick={() => setEmbed(safeEmbedUrl(viewUrl))} disabled={!viewUrl}>
            Mostrar tela
          </button>
        ) : state === "not_created" ? (
          <button type="button" className="phone-activate" onClick={() => void provision()} disabled={busy !== null}>
            {busy === "provision" ? "Provisionando…" : "Provisionar aparelho"}
          </button>
        ) : state === "exited" || state === "created" ? (
          <button type="button" className="phone-activate" onClick={() => void start()} disabled={busy !== null}>
            {busy === "start" ? "Ligando…" : "Ligar aparelho"}
          </button>
        ) : (
          <span className="phone-off-hint">
            Indisponível neste host (sem Docker/KVM). Rode num servidor Linux — ver docs/phone.md.
          </span>
        )}
      </div>

      {running && (
        <>
          <p className="phone-off-hint">
            <b>1.</b> Instale o WhatsApp · <b>2.</b> registre por SMS (vira <b>principal</b>) ·{" "}
            <b>3.</b> vincule o WAHA por QR (companion).
          </p>
          <div className="phone-footer">
            <button type="button" className="phone-reload" onClick={() => void installWa()} disabled={busy !== null}>
              {busy === "install" ? "Instalando…" : "Instalar WhatsApp"}
            </button>
            <button type="button" className="phone-reload" onClick={() => void applyProxy()} disabled={busy !== null}>
              {busy === "proxy" ? "Aplicando…" : "Aplicar proxy (campo acima)"}
            </button>
          </div>
        </>
      )}

      <div className="phone-footer">
        {embed && (
          <button type="button" className="phone-reload" onClick={() => setEmbed(null)}>
            Desligar tela
          </button>
        )}
        {running && (
          <button type="button" className="phone-reload" onClick={() => void stop()} disabled={busy !== null}>
            Desligar aparelho
          </button>
        )}
        <button type="button" className="phone-reload" onClick={() => void loadLogs()}>
          Ver logs
        </button>
      </div>

      {(err || provError) && (
        <p className="phone-off-hint" style={{ color: "var(--danger)" }}>{err || provError}</p>
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

      {logs !== null && (
        <div className="phone-logs">
          <div className="phone-logs-head">
            <span>logs</span>
            <button type="button" className="phone-reload" onClick={() => setLogs(null)}>fechar</button>
          </div>
          <pre>{logs}</pre>
        </div>
      )}
    </div>
  );
}
