using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Api.Startup;

public static class WahaWebhookEnsurer
{
    public static async Task EnsureAsync(IServiceProvider services, ILogger logger, CancellationToken ct)
    {
        var dispatch = services.GetRequiredService<IOptions<DispatchOptions>>().Value;
        var waha = services.GetRequiredService<IOptions<WahaOptions>>().Value;
        var client = services.GetRequiredService<IWahaClient>();

        var hookUrl = !string.IsNullOrWhiteSpace(waha.WebhookCallbackUrl)
            ? waha.WebhookCallbackUrl
            : null;
        if (string.IsNullOrWhiteSpace(hookUrl))
        {
            logger.LogInformation("Waha:WebhookCallbackUrl not set — skipping webhook config ensure.");
            return;
        }

        try
        {
            var ok = await client.EnsureWebhookConfiguredAsync(dispatch.SessionId, hookUrl, waha.WebhookEvents, ct);
            if (ok)
            {
                logger.LogInformation("WAHA session {Session} webhook ensured at {Url}", dispatch.SessionId, hookUrl);
            }
            else
            {
                logger.LogWarning("WAHA session {Session} not found — webhook will apply when session is created.", dispatch.SessionId);
            }
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to ensure WAHA webhook config. The hook may need manual setup via WAHA dashboard.");
        }
#pragma warning restore CA1031
    }
}
