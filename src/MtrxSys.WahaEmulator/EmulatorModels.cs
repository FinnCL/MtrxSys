namespace MtrxSys.WahaEmulator;

/// <summary>Config do emulador (env via Emulator__*, ou herdada do compose do WAHA real).</summary>
public sealed class EmulatorOptions
{
    /// <summary>Quando true, o /start do app já conecta a sessão (WORKING) e semeia grupos —
    /// é o que permite "entrar no WhatsApp sem aparelho" sem nenhum clique. False = cai no QR
    /// fake pra testar o onboarding.</summary>
    public bool AutoConnect { get; set; } = true;

    /// <summary>Número do "chip" emulado (só dígitos, DDI+DDD). Vira o "me" da sessão.</summary>
    public string MePhone { get; set; } = "5511999990000";

    public string MePushName { get; set; } = "Chip Emulado";

    /// <summary>Token opcional do webhook (X-Webhook-Token). Espelha Webhooks__WahaToken do app.</summary>
    public string? WebhookToken { get; set; }

    /// <summary>URL de webhook padrão, usada se o app ainda não gravou config na sessão.
    /// Espelha WHATSAPP_HOOK_URL (ex.: http://api:8080/webhooks/waha).</summary>
    public string? DefaultHookUrl { get; set; }
}

public sealed class Participant
{
    public required string PhoneDigits { get; set; }
    public string? PushName { get; set; }
    public bool IsAdmin { get; set; }
    public string ChatId => PhoneDigits + "@c.us";
}

public sealed class Group
{
    public required string Jid { get; set; }          // ex.: "120363000000000001@g.us"
    public required string Subject { get; set; }
    public List<Participant> Participants { get; } = [];
    public string Number => Jid.Split('@')[0];
}

public sealed class Msg
{
    public required string Id { get; set; }            // "core" do id (chave de de-dupe do app)
    public required string ChatId { get; set; }        // "<digits>@c.us"
    public bool FromMe { get; set; }
    public string? Author { get; set; }                // nome de exibição no painel
    public required string Body { get; set; }
    public long Timestamp { get; set; }                // unix seconds
}

public sealed class WebhookCfg
{
    public required string Url { get; set; }
    public List<string> Events { get; } = [];
}

public sealed class Session
{
    public required string Id { get; set; }
    public string Status { get; set; } = "STOPPED";    // STOPPED|STARTING|SCAN_QR_CODE|WORKING|FAILED
    public bool Authenticated { get; set; }
    public bool ForceScan { get; set; }                // pós-reset: força o QR no próximo start
    public string? MePhone { get; set; }
    public string? MePushName { get; set; }
    public string QrValue { get; set; } = "";
    public string? ProxyServer { get; set; }
    public List<WebhookCfg> Webhooks { get; } = [];
    public List<Group> Groups { get; } = [];
    public List<Msg> Messages { get; } = [];
}
