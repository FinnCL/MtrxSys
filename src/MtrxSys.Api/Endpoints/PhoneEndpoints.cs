using Microsoft.Extensions.Options;
using MtrxSys.Api.BackgroundServices;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

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

        // "Trocar chip": zera o WhatsApp do emulador (pm clear) → tela de boas-vindas, pronto pra
        // registrar OUTRO número. A conta velha sai do app (não do servidor). Botão "Trocar chip".
        group.MapPost("/whatsapp/reset", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(new { output = await phone.ClearWhatsAppAsync(ct) }));

        group.MapPost("/proxy", async (IPhoneOrchestrator phone, ProxyRequest req, CancellationToken ct) =>
            Results.Ok(new { output = await phone.SetProxyAsync(req.Server, ct) }));

        // Navegação do Android (back/home/recents) via adb keyevent — pros botões ◁ ○ □ da aba.
        group.MapPost("/key", async (IPhoneOrchestrator phone, string k, CancellationToken ct) =>
            Results.Ok(new { output = await phone.SendKeyAsync(k, ct) }));

        // Digita texto no campo focado do emulador (adb input text) — colar de fora (ex.: código WAHA).
        group.MapPost("/text", async (IPhoneOrchestrator phone, string t, CancellationToken ct) =>
            Results.Ok(new { output = await phone.SendTextAsync(t, ct) }));

        // Lê o número do WhatsApp registrado no emulador (registration_jid) — auto-preenche o Passo 2.
        group.MapGet("/whatsapp-number", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(new { number = await phone.GetWhatsAppNumberAsync(ct) }));

        // Vínculo por QR via DEEP LINK (sem câmera, sem rate limit — engine GOWS): reinicia a sessão
        // pra QR fresco, pega a URL do QR (https://wa.me/settings/linked_devices#...) e abre no WhatsApp
        // do emulador via intent. Aparece "Deseja conectar um dispositivo?" — o usuário toca "Continuar"
        // na tela (ganha a janela de ~20s do QR fácil, na mão). Contorna o WEBJS quebrado
        // (whatsapp-web.js) + o rate limit do pairing-code.
        group.MapPost("/link-qr", async (IPhoneOrchestrator phone, IWahaClient waha,
            IOptions<DispatchOptions> dispatch, CancellationToken ct) =>
        {
            var sessionId = dispatch.Value.SessionId;
            await waha.RestartSessionAsync(sessionId, ct);
            string? qr = null;
            for (var i = 0; i < 12 && string.IsNullOrWhiteSpace(qr); i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                if (await waha.GetSessionStatusAsync(sessionId, ct) == WahaSessionStatus.ScanQrCode)
                {
                    qr = await waha.GetQrRawAsync(sessionId, ct);
                }
            }
            if (string.IsNullOrWhiteSpace(qr))
            {
                return Results.Ok(new { ok = false, output = "sessão não chegou em SCAN_QR_CODE / sem QR." });
            }
            var output = await phone.OpenUrlAsync(qr, ct);
            return Results.Ok(new { ok = output == "ok", output });
        });

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
