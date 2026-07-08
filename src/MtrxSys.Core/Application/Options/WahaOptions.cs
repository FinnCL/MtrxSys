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

    // Engine NOWEB: markOnline=false conecta o companion PASSIVO (não se anuncia "online" ao servidor).
    // O default do WAHA é true — companion que marca online + manda typing + envia parece uma sessão de
    // BOT ATIVA, e o WhatsApp pode remover o aparelho (stream:error conflict/device_removed) numa conta
    // nova logo no 1º disparo. Passivo é o tweak de estabilidade conhecido do Baileys. Vai no config.noweb
    // (criação + PUT). null = não mexe (mantém o default true do WAHA). Setar via Waha__NowebMarkOnline.
    public bool? NowebMarkOnline { get; set; }
}
