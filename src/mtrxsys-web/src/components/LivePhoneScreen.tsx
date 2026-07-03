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
  // ws-scrcpy do LDPlayer/emulador: abre direto na tela do device; maquete/noVNC: embute a url como está.
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


      <div className="phone-footer">
        {embed ? (
          <>
            <button type="button" className="phone-reload" onClick={() => setEmbed(null)}>
              Desligar tela
            </button>
            <a className="phone-reload" href={embed} target="_blank" rel="noreferrer">
              Abrir em nova aba
            </a>
          </>
        ) : (
          // Desconectado + tela desligada: botão OPCIONAL pra abrir o emulador (instalar WhatsApp/
          // registrar). Quando conectado, o botão "Mostrar tela do Android" já vive no card acima.
          url && !connected && (
            <button
              type="button"
              className="phone-reload"
              onClick={() => setEmbed(safeEmbedUrl(androidUrl))}
            >
              Mostrar tela do Android
            </button>
          )
        )}
      </div>

      {showServerOption && (
        <>
          <button type="button" className="phone-reload" onClick={() => setShowServer((s) => !s)}>
            {showServer ? "Ocultar" : "Configurar"} aparelho no servidor
          </button>
          {showServer && <ServerAndroidPanel url={url} connected={connected} />}
        </>
      )}
    </section>
  );
}

// Opção de servidor: Android REAL em container (docker-android). A aba provisiona/liga/instala tudo
// pela API (docker.sock), sem prompt. Só funciona num host Linux com /dev/kvm — fora disso, mostra
// "indisponível" de forma limpa. Aqui o Android pode virar o dispositivo PRINCIPAL (registro por SMS).
function ServerAndroidPanel({ url, connected }: { url: string; connected: boolean }) {
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
  // Acordar o primário adormecido (keep-alive manual). Não-bloqueante: a API só agenda; o
  // PhoneKeepAliveService faz o ciclo (liga → online → desliga) em background.
  const keepAlive = () => run("keepalive", async () => { await api.phoneKeepAlive(); });

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
        Android real em container. Vira o <b>principal</b> do número (registro por SMS) e dispensa o
        físico. Exige host Linux com <b>/dev/kvm</b>.
      </p>

      {connected && !running && (
        <div className="phone-footer">
          <span className="phone-off-hint">
            💤 primário <b>dormindo</b> — o disparo roda pelo <b>WAHA</b> mesmo com o emulador desligado.
          </span>
          <button
            type="button"
            className="phone-reload"
            onClick={() => void keepAlive()}
            disabled={busy !== null}
          >
            {busy === "keepalive" ? "Acordando…" : "Acordar / Keep-alive agora"}
          </button>
        </div>
      )}

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
            <b>1.</b> Instale o WhatsApp. <b>2.</b> Registre por SMS (vira <b>principal</b>).{" "}
            <b>3.</b> Vincule o WAHA por QR (companion).
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
