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
            IOptions<WahaOptions> wahaOpts,
            ILoggerFactory logFactory,
            HttpContext http,
            CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            var status = await waha.GetSessionStatusAsync(sessionId, ct);
            if (status != WahaSessionStatus.ScanQrCode)
            {
                return Results.Problem(
                    detail: $"session not scanning (status={status})",
                    statusCode: 409);
            }
            // TRAVA anti-vazamento: NÃO serve o QR enquanto a sessão não está com o proxy aplicado.
            // Senão o chip poderia conectar (WORKING) SEM proxy — saindo pelo IP do servidor — e não dá
            // pra aplicar depois numa conta viva sem deslogar. Aplica o proxy (seguro em SCAN_QR_CODE,
            // sem conta a perder; pode reiniciar a sessão pra o proxy valer) e devolve 409 pro front
            // tentar de novo. Idempotente: uma vez aplicado, os próximos polls servem o QR direto.
            if (!await waha.IsProxyReadyAsync(sessionId, ct))
            {
                await TryEnsureWebhookAsync(waha, sessionId, wahaOpts.Value,
                    logFactory.CreateLogger("WahaQrProxyGuard"), ct);
                return Results.Problem(
                    detail: "aplicando proxy antes de parear; tente de novo",
                    statusCode: 409);
            }
            var bytes = await waha.GetQrPngAsync(sessionId, ct);
            http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
            return Results.File(bytes, "image/png");
        });

        // Alternativa ao QR: pareia por CÓDIGO de número (NOWEB). Imune ao timing de rotação do QR
        // — é o caminho robusto quando o scan não fecha (QR expira antes de o celular completar).
        // O usuário digita o código no WhatsApp em "Conectar com número de telefone".
        group.MapPost("/pairing-code", async (
            WahaPairingCodeRequest body,
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            var status = await waha.GetSessionStatusAsync(sessionId, ct);
            if (status != WahaSessionStatus.ScanQrCode)
            {
                return Results.Problem(detail: $"session not scanning (status={status})", statusCode: 409);
            }
            var digits = new string((body.PhoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
            if (digits.Length < 12)
            {
                return Results.Problem(
                    detail: "informe o número com DDI+DDD (ex.: 5571999998888)", statusCode: 400);
            }
            var code = await waha.RequestPairingCodeAsync(sessionId, digits, ct);
            return Results.Ok(new { code });
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
            await waha.EnsureWebhookConfiguredAsync(sessionId, hookUrl, opts.WebhookEvents, opts.WebhookToken, ct);
            log.LogInformation("Webhook ensured at {Url}", hookUrl);
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            log.LogWarning(ex, "Failed to ensure webhook; will retry on next status check");
        }
#pragma warning restore CA1031
    }

    public sealed record WahaPairingCodeRequest(string? PhoneNumber);
}
