using Microsoft.EntityFrameworkCore;
using MtrxSys.Infrastructure.Persistence;

namespace MtrxSys.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        // LIVENESS: só diz que o processo está de pé. É o que o docker healthcheck usa pra o gating
        // de start (não pode depender do banco, senão um blip do Postgres viraria restart em cascata).
        app.MapGet("/health", () => Results.Ok(new { status = "ok", utc = DateTimeOffset.UtcNow }))
            .AllowAnonymous();

        // READINESS: pinga o banco (timeout curto). Pra monitoramento/LB detectarem degradação em
        // RUNTIME — o /health sempre-ok escondia o caso "processo vivo, mas todo request dá 500".
        app.MapGet("/health/ready", async (MtrxDbContext db, CancellationToken ct) =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var dbOk = await db.Database.CanConnectAsync(timeout.Token);
                return dbOk
                    ? Results.Ok(new { status = "ready", db = true })
                    : Results.Json(new { status = "degraded", db = false }, statusCode: 503);
            }
#pragma warning disable CA1031 // readiness nunca lança: qualquer falha é "degraded", não 500 cru
            catch (Exception ex)
            {
                return Results.Json(new { status = "degraded", db = false, error = ex.Message }, statusCode: 503);
            }
#pragma warning restore CA1031
        }).AllowAnonymous();

        return app;
    }
}
