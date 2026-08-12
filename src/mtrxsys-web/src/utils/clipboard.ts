/**
 * Copia texto para a área de transferência e diz se conseguiu.
 *
 * Por que não é só `navigator.clipboard.writeText`: essa API só existe em contexto seguro
 * (https ou localhost). O app é aberto também pelo IP da LAN em http, e lá `navigator.clipboard`
 * vem `undefined` — o botão morreria calado justamente na máquina do operador. O caminho antigo
 * (textarea + execCommand) é deprecado mas continua funcionando em http, então serve de reserva.
 *
 * Devolve boolean em vez de lançar: quem chama precisa dizer "copiado" ou "não deu" na tela, e
 * um erro não tratado aqui viraria botão que não responde.
 */
export async function copyText(text: string): Promise<boolean> {
  if (!text) return false;

  if (navigator.clipboard?.writeText) {
    try {
      await navigator.clipboard.writeText(text);
      return true;
    } catch {
      // Permissão negada ou aba sem foco: cai na reserva abaixo.
    }
  }

  try {
    const area = document.createElement("textarea");
    area.value = text;
    // Fora da vista e sem rolar a página, mas ainda focável (display:none não copia).
    area.setAttribute("readonly", "");
    area.style.position = "fixed";
    area.style.top = "-1000px";
    area.style.opacity = "0";
    document.body.appendChild(area);
    area.select();
    const ok = document.execCommand("copy");
    document.body.removeChild(area);
    return ok;
  } catch {
    return false;
  }
}
