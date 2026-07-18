using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Webhooks;
using MtrxSys.Core.Safety;

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

        // Janela de reassentamento (settle) pro contador da UI: depois que o chip (re)conecta, o envio
        // manual fica bloqueado ~SettleAfterReconnectSeconds — mandar antes disso já restringiu o chip
        // por 7 dias (prod 2026-07-16). Expõe quanto FALTA pra liberar, pra a tela mostrar a contagem e
        // o operador não tentar enviar cedo (e levar 409 sem entender). Mesma fonte que o gate de envio
        // (SessionReadinessTracker), então a contagem bate com o que realmente destrava.
        group.MapGet("/readiness", (
            SessionReadinessTracker readiness,
            IOptions<DispatchOptions> dispatch,
            IClock clock) =>
        {
            var settle = Math.Max(0, dispatch.Value.SettleAfterReconnectSeconds);
            var workingFor = readiness.WorkingFor(clock.UtcNow);
            var elapsed = workingFor is { } w ? (int)w.TotalSeconds : (int?)null;
            var remaining = elapsed is { } e ? Math.Max(0, settle - e) : settle;
            var ready = elapsed is { } e2 && e2 >= settle;
            return Results.Ok(new
            {
                working = workingFor is not null,
                settleSeconds = settle,
                remainingSeconds = remaining,
                ready,
            });
        });

        group.MapPost("/start", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            IOptions<WahaOptions> wahaOpts,
            ILoggerFactory logFactory,
            CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            // EnsureSessionStartedAsync é robusto pra qualquer estado: recria com config completo
            // quando a sessão falta ou está "pelada", reinicia/reconecta quando já existe com config
            // (inclusive de FAILED, via restart), e NUNCA mexe numa sessão Working. Não precisa mais
            // tratar FAILED aqui — o /start puro em FAILED (que o WAHA recusa) virava sessão travada.
            await waha.EnsureSessionStartedAsync(sessionId, ct);

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
                // FECHA A JANELA config-vs-conexão: o proxy é gravado no config IMEDIATAMENTE, mas só
                // passa a valer na RECONEXÃO (o restart que o ensure dispara). Sem esperar, um poll
                // seguinte veria "config tem proxy" (IsProxyReady=true) enquanto a conexão AINDA é a
                // antiga (sem proxy) e serviria um QR não-proxied — o vazamento que a trava evita.
                // Espera a sessão SAIR de ScanQrCode (o restart derrubar a conexão antiga); ao voltar,
                // já é a conexão nova (proxied). Best-effort e curto: se não sair em ~3s, o poll seguinte
                // reavalia (o QR antigo já teria rotacionado no restart).
                for (var i = 0; i < 6 && !ct.IsCancellationRequested; i++)
                {
                    await Task.Delay(500, ct);
                    // Best-effort: um hiccup do WAHA na leitura de status (comum logo após o restart que
                    // o ensure dispara) NÃO pode virar 500 — o front só trata 409 (re-poll). Na falha,
                    // sai do loop e cai no 409 documentado; o próximo poll reavalia.
                    try
                    {
                        if (await waha.GetSessionStatusAsync(sessionId, ct) != WahaSessionStatus.ScanQrCode)
                        {
                            break;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
#pragma warning disable CA1031
                    catch
                    {
                        break;
                    }
#pragma warning restore CA1031
                }
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
