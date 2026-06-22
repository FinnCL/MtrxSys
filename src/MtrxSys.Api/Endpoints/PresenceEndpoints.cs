using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using MtrxSys.Api.Services;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Safety;

namespace MtrxSys.Api.Endpoints;

// Endpoints anônimos da feature de "card em uso" da landing multi-ambiente, baseada em
// CONEXÃO (SSE) em vez de heartbeat — o navegador segura a conexão viva mesmo com a aba
// minimizada/em segundo plano/congelada, então o card só destrava quando a aba realmente cai.
// connect: o dashboard mantém um EventSource aberto enquanto a aba existir.
// status:  a landing consulta a cada poll pra travar/destravar o card.
public static class PresenceEndpoints
{
    public static IEndpointRouteBuilder MapPresenceEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/presence/connect", async (PresenceTracker tracker, HttpContext ctx, CancellationToken ct) =>
        {
            ctx.Response.Headers.ContentType = "text/event-stream";
            ctx.Response.Headers.CacheControl = "no-cache";
            ctx.Response.Headers.Append("X-Accel-Buffering", "no");

            // Conta a conexão ANTES do primeiro write — o status já reflete "ativo" assim que
            // o stream abre. O using garante o decremento quando o cliente cai (ct cancela).
            using var _ = tracker.Connect();
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    // Keepalive (comentário SSE): segura o stream e, principalmente, faz o
                    // servidor PERCEBER rápido quando o cliente sumiu sem fechar limpo — o
                    // write falha e cancela. Fechar a aba já dispara RequestAborted na hora.
                    await ctx.Response.WriteAsync(": keepalive\n\n", ct);
                    await ctx.Response.Body.FlushAsync(ct);
                    await Task.Delay(TimeSpan.FromSeconds(10), ct);
                }
            }
            catch (OperationCanceledException)
            {
                // Cliente desconectou de forma limpa (aba fechou/navegou) — fluxo esperado.
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                // Queda abrupta do socket (reset por crash/kill) durante um write — também
                // esperado: o RequestAborted já disparou, só não queremos logar como erro.
            }
        }).AllowAnonymous();

        app.MapGet("/api/presence/status", (PresenceTracker tracker) =>
        {
            return Results.Ok(tracker.GetStatus());
        }).AllowAnonymous();

        // Status do chip pra landing pintar o selo do card. Anônimo (a landing não tem JWT).
        // Devolve { status, breakerOpen }:
        //  - status: WahaSessionStatus (Working→Pareado, resto→Desconectado). Cacheado ~5s: a
        //    landing consulta os N ambientes em loop e pode ter várias abas; sem cache cada poll
        //    bateria no WAHA. Erro/queda do WAHA → "Unknown" (landing trata como Desconectado).
        //  - breakerOpen: circuit breaker aberto = muitas falhas de envio seguidas → o chip não
        //    está disparando (a landing pinta "Chip com falha", junto do status FAILED). Leitura
        //    barata do estado singleton no banco; reabre sozinho quando o breaker fecha.
        app.MapGet("/api/presence/chip", async (
            IWahaClient waha,
            IOptions<DispatchOptions> dispatch,
            IOptions<MtrxSys.Api.Options.PresenceOptions> presence,
            CircuitBreaker breaker,
            IMemoryCache cache,
            CancellationToken ct) =>
        {
            // Status + identidade (número/nome) do chip num cache só (5s): a landing usa o número
            // pra avisar quando o MESMO contato está conectado em dois ambientes. A identidade só é
            // buscada quando Working (sessão sem número não tem "me"). Cache evita martelar o WAHA.
            var info = await cache.GetOrCreateAsync("chip-session-info", async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(5);
                try
                {
                    // Uma leitura só da sessão devolve status + número/nome (antes eram 2 GETs).
                    var snap = await waha.GetSessionSnapshotAsync(dispatch.Value.SessionId, ct);
                    return new ChipInfo(snap.Status.ToString(), snap.Identity?.PhoneE164, snap.Identity?.Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch
                {
                    return new ChipInfo(WahaSessionStatus.Unknown.ToString(), null, null);
                }
#pragma warning restore CA1031
            }) ?? new ChipInfo(WahaSessionStatus.Unknown.ToString(), null, null);

            bool breakerOpen;
            try
            {
                breakerOpen = await breaker.IsOpenAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch
            {
                breakerOpen = false; // banco indisponível: não inventa falha
            }
#pragma warning restore CA1031

            // Endurecimento opcional (deploy exposto): mascara o número na resposta anônima. Default
            // off — a landing precisa do número cheio pra detectar o mesmo chip em dois cards.
            var phone = presence.Value.MaskChipPhone
                ? MtrxSys.Api.Options.PresenceOptions.Mask(info.Phone)
                : info.Phone;
            return Results.Ok(new { status = info.Status, breakerOpen, phone, name = info.Name });
        }).AllowAnonymous();

        return app;
    }

    // Status + identidade do chip, cacheados juntos no /chip.
    private sealed record ChipInfo(string Status, string? Phone, string? Name);
}
