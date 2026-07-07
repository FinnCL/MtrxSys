namespace MtrxSys.Core.Application.Options;

// Alerta operacional (ex.: chip caiu / voltou). O watch de sessão avisa aqui.
public sealed class AlertOptions
{
    public const string SectionName = "Alert";

    // URL pra POST do alerta (compatível com Slack/Discord e relays genéricos que aceitam {text}).
    // Vazio = sem push externo (o watch ainda LOGA — visível no docker logs). Env: Alert__WebhookUrl.
    public string? WebhookUrl { get; set; }

    // Rótulo do ambiente no texto do alerta (ex.: "A"), pra distinguir os 10 stacks. Env: Alert__Label.
    public string? Label { get; set; }

    // Intervalo do watch de sessão (segundos). Clamp [15, 600].
    public int SessionCheckSeconds { get; set; } = 45;
}
