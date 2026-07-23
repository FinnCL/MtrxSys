import { useCallback, useState } from "react";
import { api } from "../api/client";
import { usePoll } from "../hooks/usePoll";
import type { HumanPhaseStatus, PhoneContact } from "../api/types";

// Card da FASE HUMANA (dias 1-3 de um chip novo), na aba Celular junto do WarmupCard. Chip frio é
// quando o WhatsApp olha mais de perto, e template automatizado como PRIMEIRA atividade da conta é
// assinatura de bot — então o disparo fica travado enquanto você conversa à mão pela aba Chat.
// O progresso é MEDIDO (o webhook já grava tudo que entra e sai deste chip); nada aqui envia nada.
// Some por completo quando a fase não se aplica (applies=false) — o caso de todo chip que já
// estava em produção antes do corte. Polling 5s e estilos .phone-* iguais aos do WarmupCard.
export function HumanPhaseCard({
  onOpenConversation,
  onBlockingChange,
  active = true,
}: {
  onOpenConversation?: (id: string) => void;
  // `false` = aba Celular montada mas fora de vista (keep-alive do App.tsx): segue montada, só para
  // de consultar. Sem isto o keep-alive faria este poll rodar pra sempre em segundo plano.
  active?: boolean;
  // Avisa a tela se a fase está SEGURANDO o disparo. Sem isto o card acima segue dizendo "Pronto
  // para disparar. Vá para a aba Disparo" enquanto nada sai — o operador acharia que quebrou.
  onBlockingChange?: (blocking: boolean) => void;
}) {
  const [status, setStatus] = useState<HumanPhaseStatus | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [picking, setPicking] = useState(false);
  const [contacts, setContacts] = useState<PhoneContact[] | null>(null);
  const [contactsError, setContactsError] = useState<string | null>(null);
  const [busyPhone, setBusyPhone] = useState<string | null>(null);
  const [busyToggle, setBusyToggle] = useState(false);
  const [manual, setManual] = useState("");

  const load = useCallback(async () => {
    try {
      const next = await api.humanPhase();
      setStatus(next);
      onBlockingChange?.(next.applies && next.satisfied !== true);
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao carregar a fase humana");
    }
  }, [onBlockingChange]);

  // Sincroniza com sistema externo (progresso medido no servidor): o setState é assíncrono
  // (pós-await), não cascateia render — uso legítimo. Mesmo padrão do WarmupCard.
  usePoll(load, 5_000, active);

  const openPicker = useCallback(async () => {
    setPicking(true);
    setContacts(null);
    setContactsError(null);
    try {
      const list = await api.phoneContacts();
      setContacts(list);
    } catch (e) {
      setContactsError(e instanceof Error ? e.message : "Falha ao ler a agenda do aparelho");
    }
  }, []);

  const addPerson = useCallback(
    async (c: PhoneContact) => {
      setBusyPhone(c.phone);
      try {
        await api.addToWarmupCircle({ phone: c.phone, name: c.name });
        await load();
        setPicking(false);
      } catch (e) {
        setContactsError(e instanceof Error ? e.message : "Falha ao adicionar");
      } finally {
        setBusyPhone(null);
      }
    },
    [load],
  );

  const addManual = useCallback(async () => {
    const phone = manual.trim();
    if (!phone) return;
    setBusyPhone(phone);
    setError(null);
    try {
      await api.addToWarmupCircle({ phone });
      setManual("");
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao adicionar");
    } finally {
      setBusyPhone(null);
    }
  }, [manual, load]);

  const toggleAutoSend = useCallback(async () => {
    if (!status) return;
    setBusyToggle(true);
    setError(null);
    try {
      // API primeiro, UI depois (só reflete se der certo) — mesmo padrão do WarmupCard/Disparo.
      await api.setHumanPhaseAutoSend(!status.autoSendEnabled);
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao alternar o envio automático");
    } finally {
      setBusyToggle(false);
    }
  }, [status, load]);

  const removePerson = useCallback(
    async (phone: string) => {
      setBusyPhone(phone);
      try {
        const members = await api.listWarmupCircle();
        const found = members.find((m) => m.phone === phone);
        if (found) await api.removeFromWarmupCircle(found.id);
        await load();
      } catch (e) {
        setError(e instanceof Error ? e.message : "Falha ao remover");
      } finally {
        setBusyPhone(null);
      }
    },
    [load],
  );

  // Enquanto carrega, não pisca card vazio. E se a fase não vale pra este chip, não existe card.
  if (!status || !status.applies) {
    return null;
  }

  const circle = status.circle ?? [];
  const done = status.satisfied === true;

  return (
    <div className="phone-server">
      <p className="phone-off-hint" style={{ textAlign: "center", margin: "0 0 8px", fontWeight: 600 }}>
        Fase Humana:{" "}
        {done ? (
          <span className="phone-badge ok">concluída</span>
        ) : (
          <span className="phone-badge off">em curso</span>
        )}
      </p>

      {done ? (
        <p className="phone-off-hint">
          Fase cumprida — o disparo está liberado e entra na curva de aquecimento normal.
        </p>
      ) : (
        <p className="phone-off-hint">
          Chip novo: o <strong>disparo fica travado</strong> até haver conversa de verdade com pessoas
          reais — ida e volta. {status.autoSendEnabled
            ? "O chip está puxando assunto sozinho; converse por cima pela aba Chat quando quiser."
            : "Converse pela aba Chat, ou deixe o chip puxar assunto (botão abaixo)."}{" "}
          A fase abre sozinha, sem clique.
        </p>
      )}

      <p className="phone-off-hint" style={{ margin: "6px 0 0" }}>
        {status.activeDays ?? 0}/{status.minDays ?? 0} dias com atividade ·{" "}
        {status.qualifiedPeople ?? 0}/{status.minPeople ?? 0} conversas com ida-e-volta
        {status.startedOn ? ` · chip de ${status.startedOn}` : ""}
      </p>

      {!done && (
        <p className="phone-off-hint" style={{ margin: "2px 0 0", opacity: 0.8 }}>
          Vale qualquer conversa de pessoa pra pessoa — a lista abaixo é só pra você não se perder.
          Cada uma precisa de {status.minOutbound ?? 0} enviadas e {status.minInbound ?? 0} recebidas.
        </p>
      )}

      {circle.length > 0 && (
        <ul className="phone-off-hint" style={{ listStyle: "none", padding: 0, margin: "8px 0 0" }}>
          {circle.map((p) => (
            <li key={p.phone} style={{ display: "flex", alignItems: "center", gap: 6 }}>
              <span aria-hidden="true">{p.qualified ? "✓" : "—"}</span>
              <span style={{ flex: 1 }}>
                {p.name ?? p.phone} ({p.outbound}↑ {p.inbound}↓)
              </span>
              {p.conversationId && onOpenConversation && (
                <button
                  type="button"
                  className="phone-reload"
                  onClick={() => onOpenConversation(p.conversationId!)}
                >
                  abrir
                </button>
              )}
              <button
                type="button"
                className="phone-reload"
                onClick={() => void removePerson(p.phone)}
                disabled={busyPhone === p.phone}
              >
                {busyPhone === p.phone ? "…" : "remover"}
              </button>
            </li>
          ))}
        </ul>
      )}

      {!done && (
        <div className="phone-footer" style={{ flexDirection: "column", gap: 4 }}>
          <button
            type="button"
            className={status.autoSendEnabled ? "phone-reload" : "phone-activate"}
            onClick={() => void toggleAutoSend()}
            disabled={busyToggle || circle.length === 0}
          >
            {busyToggle ? "…" : status.autoSendEnabled ? "Parar conversa automática" : "Conversar automaticamente"}
          </button>
          <p className="phone-off-hint" style={{ margin: 0, textAlign: "center", opacity: 0.8 }}>
            {circle.length === 0
              ? "Monte o círculo abaixo pra liberar."
              : status.autoSendEnabled
                ? "O chip escreve pro seu círculo entre 8h e 22h, com intervalos longos, e para de insistir com quem não responde. Você pode conversar por cima, à mão."
                : "Deixa o chip puxar assunto com o seu círculo. Não pula a fase: ela só fecha se as pessoas responderem de verdade."}
          </p>
        </div>
      )}

      {/* DOIS caminhos pra montar o círculo, de propósito. A agenda é o cômodo; digitar o número é
          o que impede o beco sem saída — sem chip pareado (dev) ou se o engine não marcar
          isMyContact, a agenda vem VAZIA e, sem esta entrada, não haveria como adicionar ninguém,
          o robô ficaria travado e o recurso seria inutilizável. */}
      <div className="phone-footer" style={{ gap: 4 }}>
        <input
          type="tel"
          value={manual}
          onChange={(e) => setManual(e.target.value)}
          onKeyDown={(e) => { if (e.key === "Enter") void addManual(); }}
          placeholder="+55 71 99999-8888"
          aria-label="Telefone para incluir no círculo"
          style={{ flex: 1, minWidth: 0 }}
        />
        <button
          type="button"
          className="phone-reload"
          onClick={() => void addManual()}
          disabled={!manual.trim() || busyPhone === manual.trim()}
        >
          {busyPhone === manual.trim() ? "…" : "incluir"}
        </button>
      </div>

      <div className="phone-footer">
        <button type="button" className="phone-reload" onClick={() => (picking ? setPicking(false) : void openPicker())}>
          {picking ? "Fechar agenda" : "+ Adicionar da agenda"}
        </button>
      </div>

      {picking && (
        <div style={{ marginTop: 6 }}>
          {contactsError && <p className="phone-off-hint" style={{ color: "var(--danger)" }}>{contactsError}</p>}
          {!contacts && !contactsError && <p className="phone-off-hint">Lendo a agenda do aparelho…</p>}
          {contacts?.length === 0 && (
            <p className="phone-off-hint">
              Nenhum contato salvo na agenda deste chip. Salve as pessoas no aparelho — só aparecem
              aqui os contatos <strong>salvos</strong>, não quem só apareceu num chat. Se você tem
              certeza de que salvou e mesmo assim a lista vem vazia, use o campo acima pra digitar o
              número: dá no mesmo.
            </p>
          )}
          {contacts && contacts.length > 0 && (
            <ul className="phone-off-hint" style={{ listStyle: "none", padding: 0, margin: 0, maxHeight: 180, overflowY: "auto" }}>
              {contacts.map((c) => (
                <li key={c.phone} style={{ display: "flex", alignItems: "center", gap: 6 }}>
                  <span style={{ flex: 1 }}>{c.name ?? c.phone}</span>
                  <button
                    type="button"
                    className="phone-reload"
                    onClick={() => void addPerson(c)}
                    disabled={busyPhone === c.phone || circle.some((p) => p.phone === c.phone)}
                  >
                    {busyPhone === c.phone ? "…" : circle.some((p) => p.phone === c.phone) ? "já está" : "incluir"}
                  </button>
                </li>
              ))}
            </ul>
          )}
        </div>
      )}

      {error && <p className="phone-off-hint" style={{ color: "var(--danger)" }}>{error}</p>}
    </div>
  );
}
