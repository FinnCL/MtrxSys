import { useCallback, useEffect, useState, type ReactNode } from "react";
import { api } from "../api/client";
import type { ChipIdentity } from "../api/client";
import type { FunnelRow, HumanPhaseStatus } from "../api/types";

// PLAYBOOK DOS PRIMEIROS DIAS — a tela-guia de um chip NOVO. Não é um recurso novo: orquestra numa
// sequência só o que já existe espalhado (identidade do chip, Fase Humana + círculo na aba Celular,
// funil de inbound na aba Funil), com um veredito de "pronto pra rampa?". O objetivo é fazer o chip
// PARECER uma conta normal nos primeiros dias — mão-dupla de verdade, sem template automatizado como
// 1ª atividade — pra o WhatsApp não olhar torto. Tudo aqui é MEDIDO no servidor; a tela só compõe.
// Não duplica a gestão do círculo (isso vive no HumanPhaseCard, aba Celular) — aponta pra lá.

type StepState = "done" | "todo" | "warn";

const STATE_ICON: Record<StepState, string> = { done: "✓", todo: "—", warn: "!" };

export function PlaybookScreen({
  onNavigate,
}: {
  onNavigate?: (tab: "phone" | "funnel" | "chat") => void;
}) {
  const [chip, setChip] = useState<ChipIdentity | null>(null);
  const [hp, setHp] = useState<HumanPhaseStatus | null>(null);
  const [funnel, setFunnel] = useState<FunnelRow[]>([]);
  const [busyToggle, setBusyToggle] = useState(false);

  const load = useCallback(async () => {
    // Cada chamada cai pro próprio fallback: uma fonte fora do ar (ex.: funil sem convites) não pode
    // apagar a tela inteira. O poll de 5s reconcilia sozinho quando voltar.
    const [c, h, f] = await Promise.all([
      api.phoneIdentity().catch(() => null),
      api.humanPhase().catch(() => null),
      api.funnelList().catch(() => [] as FunnelRow[]),
    ]);
    setChip(c);
    setHp(h);
    setFunnel(f);
  }, []);

  useEffect(() => {
    // Sincroniza com estado externo (medido no servidor); setState pós-await não cascateia render —
    // mesmo padrão do HumanPhaseCard/WarmupCard.
    // eslint-disable-next-line react-hooks/set-state-in-effect
    void load();
    const id = setInterval(() => void load(), 5_000);
    return () => clearInterval(id);
  }, [load]);

  const connected = chip?.status === "Working";
  const applies = hp?.applies === true;
  const circleCount = hp?.circle?.length ?? 0;
  const minPeople = hp?.minPeople ?? 5;
  const minDays = hp?.minDays ?? 3;
  const activeDays = hp?.activeDays ?? 0;
  const qualified = hp?.qualifiedPeople ?? 0;
  const autoSend = hp?.autoSendEnabled === true;
  const satisfied = hp?.satisfied === true;
  // Inbound REAL: alguém que clicou o link do funil e te escreveu (engajou/respondeu) — o sinal mais
  // forte de "conta normal" (humano de verdade escolheu falar com você), distinto do círculo.
  const realInbound = funnel.filter((r) => r.status !== "pending").length;

  const toggleAuto = useCallback(async () => {
    setBusyToggle(true);
    try {
      await api.setHumanPhaseAutoSend(!autoSend);
      await load();
    } catch {
      /* o botão volta ao estado real no próximo poll */
    } finally {
      setBusyToggle(false);
    }
  }, [autoSend, load]);

  const verdict: { cls: string; text: string } = !connected
    ? { cls: "pb-warn", text: "Conecte o chip na aba Celular pra começar os primeiros dias." }
    : !applies
      ? {
          cls: "pb-warn",
          text: "A Fase Humana não está ativa pra este chip. Sem a trava dos primeiros dias, o disparo pode sair cedo demais e queimar o número novo — ligue o corte no deploy (HumanPhase:EffectiveFrom com a data de hoje).",
        }
      : satisfied
        ? {
            cls: "pb-ok",
            text: "Primeiros dias cumpridos! O disparo está liberado e entra na curva de aquecimento normal.",
          }
        : {
            cls: "pb-info",
            text: `Em aquecimento — dia ${activeDays}/${minDays} com atividade, ${qualified}/${minPeople} conversas de ida-e-volta. O disparo abre sozinho quando fechar; não force.`,
          };

  return (
    <main className="playbook">
      <section className="pb-head">
        <h2>Primeiros dias</h2>
        <p className="muted">
          Um chip novo é quando o WhatsApp olha mais de perto. A meta destes dias é simples:{" "}
          <strong>parecer uma conta normal</strong> — receber e responder mensagens de gente de
          verdade, no ritmo de humano. Nada de disparo antes disso. Siga a lista; ela mede sozinha.
        </p>
        <div className={`pb-verdict ${verdict.cls}`}>{verdict.text}</div>
      </section>

      <ol className="pb-steps">
        <Step
          n={1}
          title="Chip conectado"
          state={connected ? "done" : "warn"}
          detail={
            connected
              ? `${chip?.name ?? chip?.phone ?? "Chip"} pareado e no ar.`
              : "Nenhum número pareado. É o pré-requisito de tudo."
          }
          action={!connected ? { label: "Ir pro Celular", onClick: () => onNavigate?.("phone") } : undefined}
        />

        <Step
          n={2}
          title="Trava dos primeiros dias (Fase Humana)"
          state={applies ? "done" : "warn"}
          detail={
            applies
              ? `Ligada${hp?.startedOn ? ` · chip de ${hp.startedOn}` : ""}. O disparo fica travado até haver conversa de verdade.`
              : "Desligada pra este chip — é config de deploy (HumanPhase:EffectiveFrom). Sem ela, o número novo fica desprotegido."
          }
        />

        <Step
          n={3}
          title="Círculo de aquecimento montado"
          state={circleCount >= minPeople ? "done" : "todo"}
          detail={`${circleCount}/${minPeople} pessoas conhecidas (suas/da equipe) que vão trocar mensagem com o chip. É a mão-dupla garantida do dia 1.`}
          action={{ label: "Montar círculo (Celular)", onClick: () => onNavigate?.("phone") }}
        />

        <Step
          n={4}
          title="Chip puxando assunto sozinho"
          state={autoSend ? "done" : "todo"}
          detail={
            autoSend
              ? "Ligado: o chip escreve pro círculo entre 8h-22h, com intervalos longos, e para de insistir com quem não responde."
              : "Opcional: deixe o chip puxar assunto com o círculo. Ele produz saída — a fase só fecha se as pessoas responderem de verdade."
          }
          action={{
            label: busyToggle ? "…" : autoSend ? "Parar" : "Ligar conversa automática",
            onClick: () => void toggleAuto(),
            disabled: busyToggle || !applies || circleCount === 0,
          }}
        />

        <Step
          n={5}
          title="Conversa de verdade acontecendo (ida e volta)"
          state={satisfied ? "done" : "todo"}
          detail={`Cada conversa qualifica com ${hp?.minOutbound ?? 3} enviadas e ${hp?.minInbound ?? 3} recebidas. Vale qualquer conversa pessoa-a-pessoa, não só o círculo.`}
          action={{ label: "Ver conversas (Chat)", onClick: () => onNavigate?.("chat") }}
        >
          <div className="pb-meters">
            <Meter label="Dias com atividade" value={activeDays} max={minDays} />
            <Meter label="Conversas ida-e-volta" value={qualified} max={minPeople} />
          </div>
        </Step>

        <Step
          n={6}
          title="Inbound real (o mais forte)"
          state={realInbound > 0 ? "done" : "todo"}
          detail={
            realInbound > 0
              ? `${realInbound} pessoa${realInbound > 1 ? "s" : ""} te escreveu pelo link do funil. Humano de verdade escolhendo falar com você é o melhor sinal de conta normal.`
              : "Distribua os links wa.me do funil (anúncio, link, e-mail) — e poste seu link num grupo que você participa (\"quem quiser, me chama: [link]\"). Quem clica e te escreve vira inbound consentido, sem 463."
          }
          action={{ label: "Abrir o Funil", onClick: () => onNavigate?.("funnel") }}
        />
      </ol>
    </main>
  );
}

function Step({
  n,
  title,
  state,
  detail,
  action,
  children,
}: {
  n: number;
  title: string;
  state: StepState;
  detail: string;
  action?: { label: string; onClick: () => void; disabled?: boolean };
  children?: ReactNode;
}) {
  return (
    <li className={`pb-step pb-${state}`}>
      <span className="pb-step-icon" aria-hidden="true">{STATE_ICON[state]}</span>
      <div className="pb-step-body">
        <div className="pb-step-title">
          <span className="pb-step-n">Passo {n}</span>
          {title}
        </div>
        <p className="pb-step-detail">{detail}</p>
        {children}
      </div>
      {action && (
        <button type="button" className="pb-step-action" onClick={action.onClick} disabled={action.disabled}>
          {action.label}
        </button>
      )}
    </li>
  );
}

function Meter({ label, value, max }: { label: string; value: number; max: number }) {
  const pct = max > 0 ? Math.min(100, Math.round((value / max) * 100)) : 0;
  return (
    <div className="pb-meter">
      <div className="pb-meter-top">
        <span>{label}</span>
        <span className="pb-meter-num">{value}/{max}</span>
      </div>
      <div className="pb-bar">
        <div className={`pb-bar-fill${value >= max ? " full" : ""}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
}
