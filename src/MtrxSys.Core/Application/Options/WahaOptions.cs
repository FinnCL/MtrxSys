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

    public string[] WebhookEvents { get; set; } = ["message", "message.any"];
}
