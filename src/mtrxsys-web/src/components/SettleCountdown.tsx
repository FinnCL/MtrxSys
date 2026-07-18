import { useEffect, useState } from "react";
import { api } from "../api/client";

// Contador da JANELA DE REASSENTAMENTO (settle). Logo depois que o chip (re)conecta — inclusive ao
// parear pelo QR —, o envio manual fica bloqueado por ~2 min: mandar antes disso é o que faz o WhatsApp
// remover o companion recém-linkado e aplicar restrição (visto em prod: "oi" 22s após parear → 7 dias).
// Sem este aviso, o operador só via um 409 sem entender. Mostra quanto falta pra liberar e some quando
// já pode enviar. Fonte: /api/waha/readiness — o MESMO tracker que destrava o envio, então a contagem
// bate com a realidade. Aparece em qualquer aba (fica no shell), pois vale pro Chat e pra tela Celular.
export function SettleCountdown() {
  const [remaining, setRemaining] = useState<number | null>(null);
  const [ready, setReady] = useState(true);
  const [working, setWorking] = useState(false);

  useEffect(() => {
    let cancelled = false;
    async function poll() {
      try {
        const r = await api.wahaReadiness();
        if (cancelled) return;
        setWorking(r.working);
        setReady(r.ready);
        // O servidor é a fonte da verdade da contagem; o tick local abaixo só suaviza entre polls.
        setRemaining(r.ready ? 0 : r.remainingSeconds);
      } catch {
        /* sem readiness (ex.: deslogado) → não mostra nada */
      }
    }
    void poll();
    const pollHandle = setInterval(poll, 3_000);
    // Desce 1s por segundo pra a contagem não travar entre os polls de 3s (reancorada a cada poll).
    const tickHandle = setInterval(
      () => setRemaining((s) => (s === null || s <= 0 ? s : s - 1)),
      1_000,
    );
    return () => {
      cancelled = true;
      clearInterval(pollHandle);
      clearInterval(tickHandle);
    };
  }, []);

  // Só aparece quando há sessão conectada AINDA reassentando. Liberado / sem sessão → nada na tela.
  if (!working || ready || remaining === null || remaining <= 0) {
    return null;
  }

  const mm = Math.floor(remaining / 60);
  const ss = remaining % 60;
  const label = mm > 0 ? `${mm}:${ss.toString().padStart(2, "0")}` : `${ss}s`;

  return (
    <div className="settle-banner" role="status" aria-live="polite">
      <span className="settle-dot" aria-hidden="true" />
      <span className="settle-text">
        Chip reconectou há pouco — <strong>aguarde {label}</strong> antes de enviar (proteção contra
        restrição de companion recém-linkado).
      </span>
    </div>
  );
}
