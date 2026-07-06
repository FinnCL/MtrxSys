import { useCallback, useEffect, useState, type CSSProperties } from "react";
import { api } from "../api/client";

type WarmupEngineStatus = Awaited<ReturnType<typeof api.warmupEngineStatus>>;

const cell: CSSProperties = { textAlign: "left", padding: "6px 12px", borderBottom: "1px solid #333" };

// Tela do MOTOR DE AQUECIMENTO DE CONVERSA (pool). UM toggle Iniciar/Parar + status por membro
// (polling 5s). O pool/grupos são configurados no servidor (WarmupEngine:*); aqui só liga/desliga
// e acompanha. Distinta da rampa de teto do Disparo.
export function WarmupScreen() {
  const [status, setStatus] = useState<WarmupEngineStatus | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const load = useCallback(async () => {
    try {
      setStatus(await api.warmupEngineStatus());
      setError(null);
    } catch (e) {
      setError(e instanceof Error ? e.message : "Falha ao carregar status");
    }
  }, []);

  useEffect(() => {
    void load();
    const id = setInterval(() => void load(), 5_000);
    return () => clearInterval(id);
  }, [load]);

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
    return (
      <main className="warmup-screen" style={{ padding: 24 }}>
        <p className="muted">{error ?? "Carregando..."}</p>
      </main>
    );
  }

  const canStart = status.featureEnabled && status.memberCount >= 2;

  return (
    <main className="warmup-screen" style={{ padding: 24, maxWidth: 760, margin: "0 auto" }}>
      <h2>Aquecimento de conta</h2>
      <p className="muted">
        O pool conversa entre si (mão dupla) e entra em grupos legítimos, pra a conta ganhar reputação de
        usuário real antes do disparo frio. Diferente da rampa de teto do Disparo.
      </p>

      {!status.featureEnabled && (
        <div style={{ padding: 12, border: "1px solid #c90", borderRadius: 8, margin: "12px 0" }}>
          Feature desligada por config (<code>WarmupEngine:Enabled=false</code>). Configure o pool no
          servidor (<code>WarmupEngine:Members</code>) pra habilitar.
        </div>
      )}

      <div style={{ display: "flex", alignItems: "center", gap: 16, margin: "20px 0" }}>
        <span style={{ fontWeight: 600, color: status.running ? "#1a9d1a" : "#888" }}>
          {status.running ? "● Aquecendo" : "○ Parado"}
        </span>
        <button type="button" onClick={() => void onToggle()} disabled={busy || (!status.running && !canStart)}>
          {busy ? "..." : status.running ? "Parar Aquecimento" : "Iniciar Aquecimento"}
        </button>
        {!canStart && !status.running && status.featureEnabled && (
          <span className="muted">Precisa de ≥ 2 membros no pool.</span>
        )}
      </div>

      {error && <p style={{ color: "#c33" }}>{error}</p>}

      <div style={{ display: "flex", gap: 32, margin: "20px 0", flexWrap: "wrap" }}>
        <Stat label="Membros do pool" value={status.memberCount} />
        <Stat label="Grupos" value={status.groupCount} />
        <Stat label="Início da rampa" value={status.startedOn ?? "—"} />
      </div>

      <h3>Atividade de hoje (por membro)</h3>
      {status.members.length === 0 ? (
        <p className="muted">
          Nenhum membro configurado. Configure <code>WarmupEngine:Members</code> no servidor.
        </p>
      ) : (
        <table style={{ width: "100%", borderCollapse: "collapse" }}>
          <thead>
            <tr>
              <th style={cell}>Membro</th>
              <th style={cell}>Número</th>
              <th style={cell}>Msgs hoje</th>
            </tr>
          </thead>
          <tbody>
            {status.members.map((m) => (
              <tr key={m.name}>
                <td style={cell}>{m.name}</td>
                <td style={cell}>{m.phone}</td>
                <td style={cell}>{m.sentToday}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </main>
  );
}

function Stat({ label, value }: { label: string; value: string | number }) {
  return (
    <div>
      <div className="muted" style={{ fontSize: 12 }}>
        {label}
      </div>
      <div style={{ fontSize: 20, fontWeight: 600 }}>{value}</div>
    </div>
  );
}
