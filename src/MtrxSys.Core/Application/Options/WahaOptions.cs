using System.ComponentModel.DataAnnotations;

namespace MtrxSys.Core.Application.Options;

public sealed class WahaOptions
{
    public const string SectionName = "Waha";

    [Required(AllowEmptyStrings = false)]
    [Url]
    public string BaseUrl { get; set; } = "http://localhost:3000";

    public string? ApiKey { get; set; }

    [Range(1, 600)]
    public int TimeoutSeconds { get; set; } = 30;

    public string? WebhookCallbackUrl { get; set; }

    // message.ack = sensor de ENTREGA (detecta shadow-restriction: mensagem sai mas não entrega).
    // Assinar isto é SEGURO pra sessões vivas: o WebhookConfigured compara só URL+token (não os
    // eventos), então adicionar aqui NÃO dispara PUT numa sessão WORKING (não mexe no proxy/logout).
    // O novo evento passa a valer no próximo pareamento/criação da sessão.
    public string[] WebhookEvents { get; set; } = ["message", "message.any", "message.ack"];

    // Token do webhook (header X-Webhook-Token). Preenchido a partir de Webhooks:WahaToken (fonte
    // única — o mesmo token que a API valida no /webhooks/waha). Quando presente, é gravado no
    // config da sessão como customHeader, pra o WAHA REAL enviá-lo em cada callback. Sem isso o
    // WAHA nunca manda o header e a validação do endpoint rejeitaria todo inbound (inclusive SAIR).
    public string? WebhookToken { get; set; }

    // Proxy por chip (anti-correlação de IP). Aplicado no CONFIG DA SESSÃO via API do WAHA —
    // NÃO na env var WHATSAPP_PROXY_SERVER, que o WAHA 2026.x (CORE/NOWEB) IGNORA silenciosamente
    // (comprovado: a sessão conecta direto pelo IP da máquina). Vazio = sem proxy (sai pela máquina).
    // Formato do server: host:porta (sem http://). Ver docs/proxy.md.
    public string? ProxyServer { get; set; }
    public string? ProxyUsername { get; set; }
    public string? ProxyPassword { get; set; }

    // Religa a sessão automaticamente quando estiver parada (Stopped), reusando a auth salva
    // (sem QR). Pensado pro disparo desassistido: se a sessão cai (restart do WAHA/stack), o chip
    // pareado volta a Working sozinho em ~1 ciclo do auto-sync, em vez de esperar clique manual.
    // NÃO mexe em Failed (fica como "chip com falha") nem força nada além de iniciar.
    public bool AutoStart { get; set; } = true;
}
