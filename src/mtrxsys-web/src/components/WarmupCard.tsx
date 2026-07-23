import { useCallback, useState } from "react";
import { api } from "../api/client";
import { usePoll } from "../hooks/usePoll";

type WarmupEngineStatus = Awaited<ReturnType<typeof api.warmupEngineStatus>>;

// Card de AQUECIMENTO DE CONVERSA embutido na aba Celular (abaixo do aparelho). O pool conversa de mão
// dupla + entra em grupos pela via do WAHA (companion) — as conversas aparecem no WhatsApp da conta
// mostrado acima. UM toggle Iniciar/Parar + status por membro (polling 5s). Pool/grupos são
// configurados no servidor (WarmupEngine:*); aqui só liga/desliga e acompanha. Reusa os estilos
// .phone-* da própria aba pra ficar coeso.
export function WarmupCard() {
  const [status, setStatus] = useState<WarmupEngineStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setStatus(await api.warmupEngineStatus());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao carregar o aquecimento");
    }
  }, []);

  usePoll(load, 5_000);

  const onToggle = useCallback(async () => {
    if (!status) return;
    setBusy(true);
    setError(null);
    try {
      // API primeiro, UI depois (só reflete se der certo) — mesmo padrão do Disparo.
      if (status.running) await api.stopWarmupEngine();
      else await api.startWarmupEngine();
      await load();
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao alternar o aquecimento");
    } finally {
      setBusy(false);
    }
  }, [status, load]);

  if (!status) {
    return null; // enquanto carrega, não pisca card vazio
  }

  const canStart = status.featureEnabled && status.memberCount >= 2;

  return (
    <div className="phone-server">
      <p className="phone-off-hint" style={{ textAlign: "center", margin: "0 0 8px", fontWeight: 600 }}>
        Aquecimento:{" "}
        {status.running ? (
          <span className="phone-badge ok">aquecendo</span>
        ) : (
          <span className="phone-badge off">parado</span>
        )}
      </p>

      {!status.featureEnabled ? (
        <p className="phone-off-hint">
          Desligado por config (<code>WarmupEngine:Enabled=false</code>). Configure o pool no servidor
          (<code>WarmupEngine:Members</code>) pra habilitar.
        </p>
      ) : (
        <>
          <p className="phone-off-hint">
            O pool conversa de mão dupla + entra em grupos legítimos — as conversas aparecem no WhatsApp
            da conta acima. Dá reputação antes do disparo frio.
          </p>
          <div className="phone-footer">
            {status.running ? (
              <button type="button" className="phone-reload" onClick={() => void onToggle()} disabled={busy}>
                {busy ? "…" : "Parar Aquecimento"}
              </button>
            ) : (
              <button
                type="button"
                className="phone-activate"
                onClick={() => void onToggle()}
                disabled={busy || !canStart}
              >
                {busy ? "…" : "Iniciar Aquecimento"}
              </button>
            )}
          </div>
          {!canStart && !status.running && (
            <p className="phone-off-hint">Precisa de ≥ 2 membros no pool (<code>WarmupEngine:Members</code>).</p>
          )}
          <p className="phone-off-hint" style={{ margin: "6px 0 0" }}>
            {status.memberCount} membros · {status.groupCount} grupos
            {status.startedOn ? ` · rampa desde ${status.startedOn}` : ""}
          </p>
          {status.members.length > 0 && (
            <ul className="phone-off-hint" style={{ listStyle: "none", padding: 0, margin: "6px 0 0" }}>
              {status.members.map((m) => (
                <li key={m.name}>
                  {m.name} ({m.phone}): {m.sentToday} msgs hoje
                </li>
              ))}
            </ul>
          )}
        </>
      )}
      {error && (
        <p className="phone-off-hint" style={{ color: "var(--danger)" }}>{error}</p>
      )}
    </div>
  );
}
