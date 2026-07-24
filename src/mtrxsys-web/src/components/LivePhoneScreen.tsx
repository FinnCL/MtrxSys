import { useCallback, useRef, useState } from "react";
import { api, type ChipIdentity, type PhoneMode, type PhoneStatus } from "../api/client";
import { usePoll } from "../hooks/usePoll";
import { HumanPhaseCard } from "./HumanPhaseCard";

// Aba "Celular": a TELA do Android (noVNC do docker-android) embutida direto na página, sem moldura.
// O emulador é o ÚNICO caminho de disparo — o app oficial dentro dele envia, e é isso que mata o 463.
// Barra de navegação Android + recarregar/abrir a tela. Só aparece onde há viewer (VITE_EMULATOR_URL).
//
// O que esta tela NÃO faz mais (removido de propósito, o emulador é o único modo):
//  • alternar Emulator/WahaOnly — o modo persistido segue sendo LIDO (é ele que o dispatcher usa),
//    mas não é alternável aqui;
//  • parear o WAHA por QR — o número vive no EMULADOR e o envio não passa pelo companion.
//    ⚠️ Sem o companion vinculado não há INBOUND: respostas não chegam ao Chat, "SAIR" por texto não
//    é detectado (só o link do opt-out) e não há acks. Re-parear exige a UI antiga (ver git).
// O backend (endpoints, orquestrador, /api/phone/mode) segue intacto; isto é só a camada de tela.
interface LivePhoneScreenProps {
  url: string; // viewer do emulador (ws-scrcpy/noVNC). Vazio = host sem emulador → sempre WAHA+físico.
  viewerKind?: string; // usado no passo do emulador (deep-link scrcpy) — ainda não neste baseline.
  udid?: string; // idem — device adb a espelhar.
  showServerOption?: boolean; // idem — setup do Android no servidor.
  onDisconnect?: () => void; // abre a confirmação de desconectar o WhatsApp (só quando conectado).
  onOpenConversation?: (id: string) => void; // atalho da Fase Humana: leva à conversa na aba Chat.
  /// A aba está à vista? `false` = continua MONTADA (o iframe do emulador mantém a sessão VNC viva),
  /// só escondida por CSS e com os polls parados. Ver o comentário do keep-alive no App.tsx.
  active?: boolean;
}

export function LivePhoneScreen({ url, onDisconnect, onOpenConversation, active = true }: LivePhoneScreenProps) {
  const [ident, setIdent] = useState<ChipIdentity | null>(null);
  // Já veio ALGUMA resposta do servidor? Sem isto o primeiro render assumia "desconectado" e piscava
  // o acordeão "Conectar o WhatsApp" antes da 1ª resposta chegar — parecia que a sessão tinha caído.
  const [identKnown, setIdentKnown] = useState(false);
  // A Fase Humana está segurando o disparo? Vem do HumanPhaseCard (que já faz o poll) em vez de um
  // fetch próprio — uma fonte só, sem segundo timer.
  const [humanPhaseBlocking, setHumanPhaseBlocking] = useState(false);
  // Modo PERSISTIDO (fonte da verdade — vem do banco via /api/phone/mode). null = carregando. Só é
  // LIDO: quem escolhe o caminho de envio é o dispatcher, e a tela não alterna mais o modo.
  const [mode, setMode] = useState<PhoneMode | null>(null);
  // Saúde de entrega (sensor anti-shadow-restriction) — dos envios das últimas 24h, quantos entregaram.
  const [delivery, setDelivery] = useState<{ sent: number; delivered: number; rate: number | null } | null>(null);
  // Status do container do emulador (running/booted) — só no modo emulador, pra saber se a tela sobe.
  const [phoneStatus, setPhoneStatus] = useState<PhoneStatus | null>(null);
  // Recarrega o iframe da tela (botão "recarregar") sem F5 na página inteira.
  const [frameKey, setFrameKey] = useState(0);
  // Provisionamento do emulador num stack que ainda NÃO tem (ex.: B..J novos): cria o container + liga
  // o modo Emulator. Sem isto não havia caminho de UI pra bootstrapar um stack sem emulador.
  const [provisioning, setProvisioning] = useState(false);
  const [provisionError, setProvisionError] = useState<string | null>(null);
  // Proxy in-guest do emulador de pé? (só no modo emulador). null = verificando; false = montando/fora
  // (NÃO registre o chip); true = seguro. O watchdog do stack monta o proxy SOZINHO ~20s após o boot —
  // este sinal só CONFIRMA pro usuário; ninguém roda comando no servidor.
  const [proxyUp, setProxyUp] = useState<boolean | null>(null);
  // Último `running` visto do container, pra detectar a VOLTA do emulador (false→true) e religar a
  // tela. Ref e não state: só é lido dentro do poll, e mudá-lo não deve provocar render.
  const runningRef = useRef<boolean | null>(null);

  const refreshIdent = useCallback(async () => {
    try {
      setIdent(await api.phoneIdentity());
    } catch {
      // MANTÉM o último estado conhecido. Antes zerava pra null, então UM poll que falhasse (blip de
      // rede, api reiniciando) já jogava a tela pra "Conectar o WhatsApp" como se o chip tivesse
      // caído. Falha de consulta não é prova de desconexão — quando o chip cai de verdade, a resposta
      // VEM, com status diferente de Working.
    } finally {
      setIdentKnown(true);
    }
  }, []);

  usePoll(refreshIdent, 5000, active);

  // Lê o modo persistido e mantém em sincronia. Só faz sentido onde há emulador disponível (url);
  // sem url a aba é sempre WAHA + físico (não há o que alternar). Falha silenciosa preserva o valor.
  usePoll(async () => {
    try {
      setMode((await api.phoneMode()).mode);
    } catch {
      /* mantém o valor atual */
    }
  }, 6000, active && !!url);

  const connected = ident?.status === "Working";

  const emulatorMode = mode === "Emulator" && !!url;

  // Saúde de entrega — só quando conectado (é o chip que dispara). Poll leve; falha silenciosa.
  usePoll(async () => {
    try {
      setDelivery(await api.deliveryHealth());
    } catch {
      /* ignora */
    }
  }, 30000, active && connected);

  // Status do container do emulador — só no modo emulador. Diz se a tela (noVNC) tem o que embutir:
  // running=false → mostra "Ligar emulador"; unavailable → host sem docker/emulador. Poll leve.
  usePoll(async () => {
    try {
      const s = await api.phoneStatus();
      setPhoneStatus(s);
      // RELIGA a tela sozinha quando o emulador VOLTA (crash + watchdog, ou "Ligar emulador"). O
      // vnc_lite não tem reconexão automática: se o container reinicia, o iframe fica num
      // "Disconnected" morto até alguém apertar "Recarregar tela". Como o poll já sabe a hora em que
      // o container voltou (running false→true), remontamos o iframe nesse instante.
      const wasRunning = runningRef.current;
      runningRef.current = s.running;
      if (wasRunning === false && s.running) {
        setFrameKey((k) => k + 1);
      }
    } catch {
      /* ignora — a tela otimista embute mesmo assim */
    }
  }, 8000, active && emulatorMode);

  // Proxy in-guest do emulador (gost+iptables DENTRO do Android) — o sinal de "seguro registrar o chip".
  // Só no modo emulador; falha silenciosa preserva o valor. É read-only: o watchdog é quem monta.
  usePoll(async () => {
    try {
      setProxyUp((await api.phoneProxyHealth()).up);
    } catch {
      /* mantém o valor atual */
    }
  }, 10000, active && emulatorMode);

  // Bootstrap de um stack SEM emulador ainda (B..J recém-criados): cria o container do Android e liga o
  // modo Emulator — aí a tela aparece pra registrar o chip. O A já tem emulador + modo Emulator, então
  // este botão NÃO aparece nele (ele cai no ramo com emulatorMode=true). Provisionar boota um Android
  // do zero (leva ~1-2 min); depois o poll de status assume e mostra a tela.
  const activateEmulator = useCallback(async () => {
    setProvisioning(true);
    setProvisionError(null);
    try {
      await api.phoneProvision();
      await api.phoneSetMode("Emulator");
      setMode("Emulator");
    } catch {
      setProvisionError("Falha ao provisionar. Cheque se o host tem docker/KVM e veja os logs do emulador.");
    } finally {
      setProvisioning(false);
    }
  }, []);

  // Bloco "provisionar" reusado nos DOIS caminhos sem-emulador (WahaOnly em coluna única e Emulator com
  // container ainda `not_created`). Fragmento (sem .phone-steps) pra o chamador embrulhar onde precisa.
  const provisionPrompt = (
    <>
      <p className="phone-off-hint" style={{ textAlign: "center" }}>
        Este ambiente ainda não tem emulador. Provisione para ver a tela, registrar o chip e disparar.
      </p>
      <button type="button" className="phone-activate" onClick={() => void activateEmulator()} disabled={provisioning}>
        {provisioning ? "Provisionando… (pode levar 1-2 min)" : "Provisionar emulador"}
      </button>
      {provisionError && (
        <p className="phone-off-hint" style={{ textAlign: "center", color: "var(--danger)" }}>{provisionError}</p>
      )}
      {/* O proxy in-guest sobe AUTOMÁTICO (o watchdog do stack, sempre rodando, monta o gost+iptables
          ~20s após o boot). O usuário NÃO roda nada. Só precisa esperar o sinal "Proxy do emulador: OK"
          na tela antes de registrar — senão o número sairia pelo IP do datacenter (ban). */}
      <p className="phone-off-hint" style={{ textAlign: "center", maxWidth: 320, margin: "8px auto 0" }}>
        Ao provisionar, o proxy do emulador sobe sozinho (~20s após o boot). Só registre o chip quando
        aparecer <b>Proxy do emulador: OK</b> na tela — o sinal de que o número sai pelo IP brasileiro, não
        pelo do datacenter.
      </p>
    </>
  );

  // Os controles são montados UMA vez e compostos em dois layouts (coluna única sem emulador; tela +
  // faixa lateral com emulador). Sem isto o JSX teria que ser duplicado nos dois caminhos.
  const controls = (
    <>
      {/* Proxy REALMENTE aplicado na sessão WAHA do chip (verde) ou saída pelo IP da máquina (cinza). */}
      <p className="phone-off-hint" style={{ textAlign: "center", margin: "0 0 8px" }}>
        Proxy:{" "}
        {ident?.proxy ? (
          <span className="phone-badge ok">ativo {ident.proxy}</span>
        ) : (
          <span className="phone-badge off">desligado (sai pelo IP da máquina)</span>
        )}
      </p>

      {/* Proxy in-guest do EMULADOR (gost+iptables DENTRO do Android) — diferente do proxy da sessão WAHA
          acima. É o gate de "seguro registrar o chip": o watchdog do stack monta sozinho ~20s após o boot;
          este badge só CONFIRMA. Verde = egresso pelo residencial; senão o registro sairia pelo datacenter. */}
      {emulatorMode && (
        <p className="phone-off-hint" style={{ textAlign: "center", margin: "0 0 8px" }}>
          Proxy do emulador:{" "}
          {proxyUp === null ? (
            <span className="phone-badge">verificando…</span>
          ) : proxyUp ? (
            <span className="phone-badge ok">OK — seguro registrar o chip</span>
          ) : (
            <span className="phone-badge off">montando… NÃO registre ainda (sairia pelo datacenter)</span>
          )}
        </p>
      )}

      {/* SEM seletor de modo: o emulador é o único caminho de disparo. O modo persistido (phone_mode)
          segue sendo LIDO — é ele que o dispatcher usa pra escolher o caminho de envio — mas não é
          mais alternável pela tela. Se algum dia o banco voltar pra WahaOnly, a tela do emulador
          some (emulatorMode fica false) e o ajuste é no banco/endpoint, não aqui. */}

      {/* Identidade do companion. Conectado → mostra número e saúde de entrega. Sem pareamento por QR:
          o número vive no EMULADOR, e o envio não depende do companion. */}
      {!identKnown ? (
        // Ainda não sabemos: NÃO afirme "desconectado" (era o que fazia o acordeão "Conectar o
        // WhatsApp" piscar a cada volta pra aba, parecendo queda de sessão).
        <p className="phone-off-hint" style={{ textAlign: "center" }}>Verificando o WhatsApp…</p>
      ) : connected ? (
        <div className="phone-ident-card">
          <span className="phone-badge ok">conectado</span>
          <p className="phone-ident-name">{ident?.name || "WhatsApp conectado"}</p>
          <p className="phone-ident-phone">{ident?.phone ?? ""}</p>
          {/* Durante a Fase Humana o disparo está TRAVADO — dizer "pronto para disparar" aqui faria
              o operador ir à aba Disparo, mandar, e não ver nada sair. O card logo abaixo explica. */}
          <p className="phone-off-hint" style={{ margin: "2px 0 0" }}>
            {ident?.proxy ? "Proxy ativo. " : ""}
            {humanPhaseBlocking
              ? "Em fase de aquecimento humano — o disparo abre quando a fase fechar (veja abaixo)."
              : <>Pronto para disparar. Vá para a aba <b>Disparo</b>.</>}
          </p>
          {/* Sensor de entrega: só aparece quando JÁ HOUVE entrega confirmada (delivered > 0) — assim
              não mostra "0%" enganoso numa sessão que ainda não assina message.ack (pareada antes do
              sensor). Com ACKs fluindo, uma taxa que cai é sinal de possível restrição. */}
          {delivery && delivery.delivered > 0 && (
            <p className="phone-off-hint" style={{ margin: "2px 0 0" }}>
              Entregas 24h: <b>{delivery.delivered}/{delivery.sent}</b>
              {delivery.rate != null ? ` (${Math.round(delivery.rate * 100)}%)` : ""}
            </p>
          )}
          {onDisconnect && (
            <button type="button" className="disconnect-btn phone-disconnect" onClick={onDisconnect}>
              Desconectar WhatsApp
            </button>
          )}
        </div>
      ) : null}

      {/* FASE HUMANA (dias 1-3 do chip novo): é do CHIP, não do emulador. Só faz sentido com chip
          conectado — sem chip não há fase. O card some sozinho quando não se aplica. */}
      {connected && (
        <HumanPhaseCard onOpenConversation={onOpenConversation} onBlockingChange={setHumanPhaseBlocking} active={active} />
      )}

      {/* Legenda + ações da tela ficam AQUI (na faixa lateral) e não embaixo do emulador: cada linha
          de texto sob a tela custa largura, porque a tela é retrato e sai da altura disponível. */}
      {emulatorMode && (!phoneStatus || phoneStatus.running) && (
        // Coluna, não parágrafo com elementos soltos: o texto corrido "empurrava" o botão e o link pra
        // mesma linha, e em faixa estreita o botão acabava no meio da frase. Cada item na sua linha.
        <div className="phone-screen-actions">
          <p className="phone-off-hint">Tela do emulador. O disparo a frio sai por aqui, sem 463.</p>
          <button type="button" className="phone-reload" onClick={() => setFrameKey((k) => k + 1)}>
            Recarregar tela
          </button>
          <a href={url} target="_blank" rel="noreferrer" className="phone-screen-link">
            abrir em nova aba
          </a>
        </div>
      )}

      {/* Aquecimento de conversa (pool) — só no modo Com emulador; fora da área WAHA + físico. */}

    </>
  );

  // Escondida por CSS (e não desmontada) quando a aba sai de vista: é o que mantém a sessão VNC do
  // iframe viva entre trocas de aba.
  const hidden = active ? "" : " is-hidden";

  // Sem emulador ATIVO: coluna única. Se o stack TEM viewer (url) mas o emulador ainda não foi
  // provisionado/ligado (modo ≠ Emulator — típico de um stack novo B..J), oferece o botão de
  // provisionar — o único caminho de UI pra bootstrapar o emulador daquele ambiente.
  if (!emulatorMode) {
    return (
      <section className={`live-phone${hidden}`}>
        {url && <div className="phone-steps">{provisionPrompt}</div>}
        {controls}
      </section>
    );
  }

  // Modo "Com emulador": a TELA do Android (noVNC do docker-android) embutida direto, sem moldura.
  // O disparo a frio sai por ESTE aparelho (o primário) — é o que mata o 463. Layout LADO A LADO: a
  // tela fica com a coluna inteira (altura cheia) e os controles vão pra faixa lateral rolável.
  // Como a tela é retrato e HEIGHT-bound, altura livre é a única coisa que a faz crescer.
  return (
    <section className={`live-phone live-phone--split${hidden}`}>
      <div className="phone-main">
        {phoneStatus && !phoneStatus.running ? (
          <div className="phone-steps">
            {phoneStatus.state === "unavailable" ? (
              <p className="phone-off-hint" style={{ textAlign: "center" }}>
                Emulador indisponível neste host (sem docker/KVM).
              </p>
            ) : phoneStatus.state === "not_created" ? (
              // Container NUNCA criado (stack novo em modo Emulator): "Ligar" faria `docker start` e
              // falharia — o certo é PROVISIONAR (cria o Android do zero). Ver ProvisionAsync.
              provisionPrompt
            ) : (
              // Container EXISTE mas está parado (exited/created): aí sim "Ligar" (docker start) resolve.
              <>
                <p className="phone-off-hint" style={{ textAlign: "center" }}>
                  O emulador está desligado. Ligue para ver a tela e disparar por ele.
                </p>
                <button type="button" className="phone-activate" onClick={() => void api.phoneStart()}>
                  Ligar emulador
                </button>
              </>
            )}
          </div>
        ) : (
          <>
            {/* Sem moldura de celular: só a tela. O wrapper existe apenas pra manter o aspect-ratio
                do display e o recorte (overflow) do noVNC — nada de bezel/notch desenhado. */}
            <div className="phone-screen">
              <iframe
                key={frameKey}
                className="phone-stage"
                src={url}
                title="Tela do emulador Android (WhatsApp primário)"
                allow="clipboard-read; clipboard-write"
              />
            </div>
            {/* Navegação do Android (◁ ○ ▢) via adb keyevent — pra operar a tela quando o mouse do
                noVNC não basta (ex.: voltar de um menu). Fica colada na tela, como no aparelho. */}
            <div className="phone-navbar">
              {/* aria-label além do title: sem ele o leitor de tela anuncia só o glifo ("◁"). */}
              <button type="button" className="phone-nav-btn" title="Voltar" aria-label="Voltar" onClick={() => void api.phoneKey("back")}>◁</button>
              <button type="button" className="phone-nav-btn" title="Início" aria-label="Início" onClick={() => void api.phoneKey("home")}>○</button>
              <button type="button" className="phone-nav-btn" title="Recentes" aria-label="Recentes" onClick={() => void api.phoneKey("recents")}>▢</button>
            </div>
          </>
        )}
      </div>
      <aside className="phone-side">{controls}</aside>
    </section>
  );
}
