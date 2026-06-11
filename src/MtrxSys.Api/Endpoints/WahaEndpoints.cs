using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Webhooks;

namespace MtrxSys.Api.Endpoints;

public static class WahaEndpoints
{
    public static IEndpointRouteBuilder MapWahaEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/waha");

        group.MapGet("/status", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            var status = await waha.GetSessionStatusAsync(dispatch.Value.SessionId, ct);
            return Results.Ok(new { status = status.ToString(), session = dispatch.Value.SessionId });
        });

        group.MapPost("/start", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            IOptions<WahaOptions> wahaOpts,
            ILoggerFactory logFactory,
            CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            // De FAILED, o WAHA rejeita um simples /start (422) — só recupera com restart
            // (stop+start). Senão, garante a sessão iniciada normalmente.
            var current = await waha.GetSessionStatusAsync(sessionId, ct);
            if (current == WahaSessionStatus.Failed)
            {
                await waha.RestartSessionAsync(sessionId, ct);
            }
            else
            {
                await waha.EnsureSessionStartedAsync(sessionId, ct);
            }

            await TryEnsureWebhookAsync(waha, sessionId, wahaOpts.Value, logFactory.CreateLogger("WahaStart"), ct);

            var status = await waha.GetSessionStatusAsync(sessionId, ct);
            return Results.Ok(new { status = status.ToString() });
        });

        // Desconecta o número da sessão (logout no WhatsApp). Depois o status cai pra
        // parado/scan e a tela de conexão reaparece pra parear outro celular.
        group.MapPost("/logout", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            await waha.LogoutSessionAsync(dispatch.Value.SessionId, ct);
            var status = await waha.GetSessionStatusAsync(dispatch.Value.SessionId, ct);
            return Results.Ok(new { status = status.ToString() });
        });

        // Reset completo: desconecta E apaga a sessão (credenciais em disco), depois recria.
        // Sem o delete, o WAHA restaura o número antigo do volume e nunca mostra um QR novo —
        // é o que deixa o pareamento dinâmico (o próximo QR vale, o antigo é descartado). Os
        // contatos/conversas ficam no Postgres, intactos; isto só zera a sessão do WhatsApp.
        group.MapPost("/reset", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            IOptions<WahaOptions> wahaOpts,
            ILoggerFactory logFactory,
            CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            await waha.LogoutSessionAsync(sessionId, ct);
            await waha.DeleteSessionAsync(sessionId, ct);
            // Recria a sessão: sem credenciais, o WAHA vai pra ScanQrCode com um QR novo.
            await waha.EnsureSessionStartedAsync(sessionId, ct);
            await TryEnsureWebhookAsync(waha, sessionId, wahaOpts.Value, logFactory.CreateLogger("WahaReset"), ct);

            var status = await waha.GetSessionStatusAsync(sessionId, ct);
            return Results.Ok(new { status = status.ToString() });
        });

        group.MapGet("/qr.png", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            HttpContext http,
            CancellationToken ct) =>
        {
            var status = await waha.GetSessionStatusAsync(dispatch.Value.SessionId, ct);
            if (status != WahaSessionStatus.ScanQrCode)
            {
                return Results.Problem(
                    detail: $"session not scanning (status={status})",
                    statusCode: 409);
            }
            var bytes = await waha.GetQrPngAsync(dispatch.Value.SessionId, ct);
            http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            return Results.File(bytes, "image/png");
        });

        group.MapPost("/sync", async (
            int? messagesPerChat,
            WhatsAppSyncService sync,
            CancellationToken ct) =>
        {
            var perChat = Math.Clamp(messagesPerChat ?? 50, 1, 200);
            var result = await sync.SyncAsync(perChat, ct);
            return Results.Ok(result);
        });

        return app;
    }

    // Reaplica o webhook na sessão (best-effort): falha aqui não derruba o start/reset — o
    // ensurer de startup e o próximo /status tentam de novo. Compartilhado por /start e /reset.
    private static async Task TryEnsureWebhookAsync(
        IWahaClient waha, string sessionId, WahaOptions opts, ILogger log, CancellationToken ct)
    {
        var hookUrl = opts.WebhookCallbackUrl;
        if (string.IsNullOrWhiteSpace(hookUrl))
        {
            return;
        }
        try
        {
            await waha.EnsureWebhookConfiguredAsync(sessionId, hookUrl, opts.WebhookEvents, ct);
            log.LogInformation("Webhook ensured at {Url}", hookUrl);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to ensure webhook; will retry on next status check");
        }
#pragma warning restore CA1031
    }
}
