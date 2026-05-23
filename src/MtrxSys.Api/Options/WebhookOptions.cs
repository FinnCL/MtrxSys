namespace MtrxSys.Api.Options;

public sealed class WebhookOptions
{
    public const string SectionName = "Webhooks";
    public string? WahaToken { get; init; }
}
