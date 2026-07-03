using MtrxSys.Api.BackgroundServices;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Api.Endpoints;

/// <summary>Controle do aparelho virtual pela aba "Celular" — provisionar/ligar/parar/status/logs +
/// instalar o WhatsApp e aplicar proxy, tudo no Android em container. Exige autenticação
/// (FallbackPolicy do Program). Tudo fica DENTRO da página: o front chama estes endpoints e embute a
/// tela (noVNC) no iframe — sem prompt/script externo.</summary>
public static class PhoneEndpoints
{
    public sealed record ProxyRequest(string? Server);

    public static IEndpointRouteBuilder MapPhoneEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/phone");

        group.MapGet("/status", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(await phone.GetStatusAsync(ct)));

        group.MapGet("/booted", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(new { booted = await phone.IsBootedAsync(ct) }));

        group.MapPost("/provision", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(await phone.ProvisionAsync(ct)));

        group.MapPost("/start", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(await phone.StartAsync(ct)));

        group.MapPost("/stop", async (IPhoneOrchestrator phone, CancellationToken ct) =>
        {
            await phone.StopAsync(ct);
            return Results.NoContent();
        });

        group.MapGet("/logs", async (IPhoneOrchestrator phone, int? tail, CancellationToken ct) =>
            Results.Ok(new { logs = await phone.GetLogsAsync(tail ?? 200, ct) }));

        group.MapPost("/whatsapp/install", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(new { output = await phone.InstallWhatsAppAsync(ct) }));

        group.MapPost("/proxy", async (IPhoneOrchestrator phone, ProxyRequest req, CancellationToken ct) =>
            Results.Ok(new { output = await phone.SetProxyAsync(req.Server, ct) }));

        // Agenda um keep-alive imediato (acordar o primário adormecido). Não-bloqueante: o wake leva
        // minutos, então o PhoneKeepAliveService pega o sinal no próximo tick e roda o ciclo.
        group.MapPost("/keepalive", (PhoneKeepAliveSignal signal) =>
        {
            signal.RequestNow();
            return Results.Accepted();
        });

        return app;
    }
}
