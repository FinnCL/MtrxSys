using MtrxSys.Api.Services;

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

        return app;
    }
}
