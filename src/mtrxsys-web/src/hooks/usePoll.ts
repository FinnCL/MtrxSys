import { useEffect, useRef } from "react";

/**
 * Poll de LEITURA: roda `tick` na montagem e a cada `intervalMs`, e PAUSA enquanto a aba está oculta
 * (`document.hidden`) — ao voltar pra frente, dispara na hora e retoma o ciclo.
 *
 * Por que existe: a aba Celular sozinha mantinha 6 `setInterval` copiados (identidade 5s, modo 6s,
 * status do emulador 8s, entregas 30s, aquecimento 5s, fase humana 5s). Uma aba esquecida aberta
 * batia na API pra sempre, ×10 stacks. Aqui o custo cai a zero quando ninguém está olhando.
 *
 * `tick` fica numa ref: trocar a identidade da função NÃO reinicia o ciclo. Na versão anterior o
 * `setInterval` vivia dentro de um `useEffect([callback])` — bastava o callback mudar de identidade
 * (ex.: `useCallback` que depende de uma prop) pra o timer ser destruído e recriado, e um callback
 * recriado a cada render fazia o poll NUNCA disparar.
 *
 * Só use com leituras idempotentes (GET): pausar/retomar provoca chamadas extras.
 *
 * @param enabled `false` desliga o poll (ex.: só consultar entregas com o chip conectado).
 */
export function usePoll(tick: () => void | Promise<void>, intervalMs: number, enabled = true) {
  const tickRef = useRef(tick);
  // Atualiza em efeito (não durante o render) — escrever em ref no corpo do componente não é seguro
  // sob renderização concorrente. Declarado ANTES do efeito do timer, então roda antes dele.
  useEffect(() => {
    tickRef.current = tick;
  });

  useEffect(() => {
    if (!enabled) return;
    let timer: ReturnType<typeof setInterval> | undefined;
    let inFlight = false;
    let release: ReturnType<typeof setTimeout> | undefined;
    // Destrava o guard mesmo se o tick NUNCA resolver. O client.ts chama fetch sem AbortSignal, então
    // uma requisição pendurada não rejeita sozinha em tempo hábil — sem este teto, o guard abaixo
    // congelaria o poll pra sempre. Aqui o pior caso volta a ser o comportamento antigo (empilhar),
    // nunca "a tela parou de atualizar e ninguém percebeu".
    const releaseAfter = Math.max(intervalMs * 3, 15_000);
    const run = () => {
      // PULA o ciclo se o anterior ainda não voltou. Sem isto, quando a API demora mais que o
      // intervalo (WAHA travado é justamente quando /presence/chip fica lento), as chamadas se
      // EMPILHAM e as respostas chegam FORA DE ORDEM: a de t=0 sobrescreve a de t=5 e a tela volta
      // pro passado. No caso do chip isso vira "desconectado" na tela com o chip CONECTADO — e o
      // operador pode tentar reparear (reset re-pareia e PERDE o número). Uma por vez mata os dois.
      if (inFlight) return;
      inFlight = true;
      const done = () => {
        clearTimeout(release);
        inFlight = false;
      };
      release = setTimeout(done, releaseAfter);
      try {
        // .catch antes do .finally: o tick já trata o próprio erro; aqui só evitamos que uma rejeição
        // vire "unhandled rejection" no console do operador.
        void Promise.resolve(tickRef.current()).catch(() => {}).finally(done);
      } catch {
        done(); // tick que lançou de forma SÍNCRONA (antes de virar promise)
      }
    };
    const start = () => {
      if (timer !== undefined) return;
      run(); // volta da aba oculta = dado fresco imediato, sem esperar um ciclo inteiro
      timer = setInterval(run, intervalMs);
    };
    const stop = () => {
      if (timer === undefined) return;
      clearInterval(timer);
      timer = undefined;
    };
    const onVisibility = () => (document.hidden ? stop() : start());

    if (!document.hidden) start();
    document.addEventListener("visibilitychange", onVisibility);
    return () => {
      document.removeEventListener("visibilitychange", onVisibility);
      clearTimeout(release);
      stop();
    };
  }, [intervalMs, enabled]);
}
