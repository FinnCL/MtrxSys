import { useCallback, useEffect, useRef, useState } from "react";
import { api } from "../api/client";

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

// Passo do checklist do "Provisionar número". "wait" = esperando você (registro SMS / scan do QR).
export type StepState = "idle" | "running" | "done" | "error" | "wait";
export interface ProvStep {
  key: string;
  label: string;
  state: StepState;
  detail?: string;
}
export const STEP_ICON: Record<StepState, string> = {
  idle: "○",
  running: "◐",
  done: "✓",
  error: "✕",
  wait: "→",
};

/**
 * Máquina de estado do botão "Provisionar número": encadeia os passos AUTOMÁTICOS
 * (ligar→bootar→instalar→proxy) e deixa os MANUAIS (registro por SMS, vincular o WAHA por QR) como
 * checkpoints. Ordem proposital: o emulador abre PRIMEIRO; o QR do WAHA é o último passo, lido por
 * dentro dele. Extraído do ServerAndroidPanel pra separar orquestração de apresentação (SRP).
 */
export function useProvisionFlow(proxy: string, onRefresh: () => Promise<void>) {
  const [steps, setSteps] = useState<ProvStep[] | null>(null);
  const [linkQr, setLinkQr] = useState<string | null>(null);
  const [provBusy, setProvBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);

  // Refs pra ler sempre o valor mais novo dentro dos callbacks estáveis (sem recriá-los a cada render).
  const proxyRef = useRef(proxy);
  proxyRef.current = proxy;
  const refreshRef = useRef(onRefresh);
  refreshRef.current = onRefresh;

  // O loop de boot (até 3 min) não deve dar setState após desmontar; e o object URL do QR precisa revoke.
  const aliveRef = useRef(true);
  const linkQrRef = useRef<string | null>(null);
  useEffect(
    () => () => {
      aliveRef.current = false;
      if (linkQrRef.current) {
        URL.revokeObjectURL(linkQrRef.current);
      }
    },
    [],
  );

  const patchStep = useCallback((key: string, patch: Partial<ProvStep>) => {
    setSteps((cur) => (cur ? cur.map((s) => (s.key === key ? { ...s, ...patch } : s)) : cur));
  }, []);

  const provisionNumber = useCallback(async () => {
    const proxyNow = proxyRef.current;
    setError(null);
    setLinkQr(null);
    setProvBusy(true);
    setSteps([
      { key: "boot", label: "Ligar e bootar o Android", state: "running" },
      { key: "install", label: "Instalar o WhatsApp", state: "idle" },
      { key: "proxy", label: proxyNow ? "Aplicar proxy (IP do chip)" : "Proxy (pulado — sem IP)", state: "idle" },
      { key: "sms", label: "Registrar o número por SMS (na tela acima)", state: "idle" },
      { key: "waha", label: "Vincular o WAHA (escanear o QR na tela acima)", state: "idle" },
    ]);
    try {
      // 1) provisiona/liga e espera o Android bootar de verdade (~1-2 min no KVM).
      await api.phoneProvision();
      let booted = false;
      for (let i = 0; i < 60 && !booted; i++) {
        await sleep(3000);
        if (!aliveRef.current) return; // painel desmontou — aborta o loop sem mexer no estado
        try {
          booted = (await api.phoneBooted()).booted;
        } catch {
          booted = false;
        }
        patchStep("boot", { detail: `aguardando boot… ${(i + 1) * 3}s` });
      }
      if (!booted) {
        patchStep("boot", { state: "error", detail: "Android não bootou a tempo — veja os logs e tente de novo." });
        return;
      }
      patchStep("boot", { state: "done", detail: "" });
      await refreshRef.current();

      // 2) instala o WhatsApp (adb install) — com retry, o device pode demorar a aceitar.
      patchStep("install", { state: "running" });
      let inst = "";
      for (let i = 0; i < 3; i++) {
        inst = (await api.phoneInstallWhatsApp()).output || "";
        if (/success/i.test(inst)) break;
        await sleep(4000);
      }
      const okInstall = /success/i.test(inst);
      patchStep("install", { state: okInstall ? "done" : "error", detail: inst.slice(0, 140) });
      if (!okInstall) return;

      // 3) proxy (opcional — o mesmo IP do chip/WAHA).
      if (proxyNow) {
        patchStep("proxy", { state: "running" });
        const out = (await api.phoneSetProxy(proxyNow)).output || "";
        patchStep("proxy", { state: "done", detail: out.slice(0, 100) });
      } else {
        patchStep("proxy", { state: "done" });
      }

      // 4) registro por SMS — MANUAL na tela embutida. Marque feito pra liberar o passo do WAHA.
      patchStep("sms", {
        state: "wait",
        detail: "Na tela acima: abra o WhatsApp e registre o número por SMS. Depois clique em “Registrei”.",
      });
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setProvBusy(false);
    }
  }, [patchStep]);

  // Checkpoint do SMS: confirma o registro e gera o QR do WAHA pra ser lido DENTRO do emulador.
  const confirmSms = useCallback(async () => {
    patchStep("sms", { state: "done", detail: "" });
    patchStep("waha", { state: "wait", detail: "Gerando o QR do WAHA…" });
    try {
      const qr = await api.wahaQrBlobUrl();
      if (linkQrRef.current) {
        URL.revokeObjectURL(linkQrRef.current); // libera o QR anterior antes de trocar
      }
      linkQrRef.current = qr;
      setLinkQr(qr);
      patchStep("waha", {
        state: "wait",
        detail: "Na tela acima: WhatsApp → Aparelhos conectados → Conectar um aparelho → aponte pra este QR.",
      });
    } catch (e) {
      patchStep("waha", { state: "error", detail: e instanceof Error ? e.message : String(e) });
    }
  }, [patchStep]);

  const confirmWaha = useCallback(
    () => patchStep("waha", { state: "done", detail: "WAHA vinculado — disparo liberado." }),
    [patchStep],
  );

  return { steps, linkQr, provBusy, error, provisionNumber, confirmSms, confirmWaha };
}
