using Microsoft.Extensions.Options;
using MtrxSys.Api.BackgroundServices;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Api.Endpoints;

/// <summary>Controle do aparelho virtual pela aba "Celular" — provisionar/ligar/parar/status/logs +
/// instalar o WhatsApp e aplicar proxy, tudo no Android em container. Exige autenticação
/// (FallbackPolicy do Program). Tudo fica DENTRO da página: o front chama estes endpoints e embute a
/// tela (noVNC) no iframe — sem prompt/script externo.</summary>
public static class PhoneEndpoints
{
    public sealed record ProxyRequest(string? Server);
    public sealed record ModeRequest(string? Mode);
    public sealed record WhatsAppSendRequest(string? Phone, string? Text);

    public static IEndpointRouteBuilder MapPhoneEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/phone");

        group.MapGet("/status", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(await phone.GetStatusAsync(ct)));

        // Modo da aba "Celular" (toggle único), PERSISTIDO no system_state — a fonte da verdade do que a
        // página renderiza (independe do container do emulador estar de pé, ao contrário do /status).
        group.MapGet("/mode", async (ISystemStateRepository stateRepo, CancellationToken ct) =>
        {
            var st = await stateRepo.GetAsync(ct);
            return Results.Ok(new { mode = st.DispatchMode.ToString() });
        });

        group.MapPost("/mode", async (ModeRequest req, ISystemStateRepository stateRepo, IUnitOfWork uow,
            CancellationToken ct) =>
        {
            if (!Enum.TryParse<PhoneDispatchMode>(req.Mode, ignoreCase: true, out var mode))
            {
                return Results.BadRequest(new { error = $"modo inválido: '{req.Mode}' (use WahaOnly ou Emulator)." });
            }
            var st = await stateRepo.GetAsync(ct);
            st.SetDispatchMode(mode);
            await stateRepo.UpdateAsync(st, ct);
            await uow.SaveChangesAsync(ct);
            return Results.Ok(new { mode = mode.ToString() });
        });

        // Proxy in-guest do emulador de pé? O SINAL de "seguro registrar o chip" pra a aba Celular — o
        // egresso sai pelo residencial, não pelo IP do datacenter. false = ainda montando / fora (a UI segura).
        group.MapGet("/proxy-health", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(new { up = await phone.IsEgressProxyUpAsync(ct) }));

        // Reset FORTE do emulador (aparelho novo): rm container + rm volume + provisiona do zero. Nível 2
        // do "trocar chip" — só quando há suspeita de correlação por device. Ver ResetEmulatorAsync.
        // TRAVA: se o emulador é gerenciado por compose (ex.: o A, via emulator-a.yml), o reset por
        // docker-run recriaria um container ERRADO → bloqueia e aponta o caminho certo (ver
        // IsComposeManagedAsync). Assim o usuário não quebra o A clicando aqui.
        group.MapPost("/reset-emulator", async (IPhoneOrchestrator phone, CancellationToken ct) =>
        {
            if (await phone.IsComposeManagedAsync(ct))
            {
                return Results.Ok(new
                {
                    blocked = true,
                    message = "Este emulador é gerenciado pelo deploy (compose) — resetar por aqui o "
                        + "recriaria errado. Para um número novo, use \"Trocar chip\". Para aparelho novo, "
                        + "recrie pelo deploy/compose.",
                    status = (PhoneStatus?)null,
                });
            }
            return Results.Ok(new { blocked = false, message = (string?)null, status = (PhoneStatus?)await phone.ResetEmulatorAsync(ct) });
        });

        // Status da defesa anti-463 (Google sync) DESTE stack, pro selo na aba Guia Google. `active` = o
        // provider Google está ligado (Provider=Google + RefreshToken → auto-liga; ver DependencyInjection).
        // A invisibilidade disso foi o que deixou o chip do A disparar com a defesa apagada.
        group.MapGet("/gsync-status", (IContactAddressBookSync addressBook, IOptions<AddressBookSyncOptions> abOpts) =>
        {
            var o = abOpts.Value;
            return Results.Ok(new
            {
                active = addressBook.IsEnabled,
                provider = o.Provider,
                tokenPresent = !string.IsNullOrWhiteSpace(o.Google.RefreshToken),
            });
        });

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

        // CAMINHO A anti-463: envia UMA mensagem pela UI do WhatsApp do emulador (o PRIMÁRIO), NÃO pelo
        // WAHA. Pra TESTAR/ajustar antes de reescrever o disparo em massa: manda pra 1 número e você vê
        // se saiu (e ajusta WhatsApp:SendButtonX/Y se o toque não cair no botão enviar).
        group.MapPost("/whatsapp/send", async (IPhoneOrchestrator phone, WhatsAppSendRequest req, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(req.Phone) || string.IsNullOrWhiteSpace(req.Text))
            {
                return Results.BadRequest(new { error = "phone e text são obrigatórios." });
            }
            var r = await phone.SendWhatsAppMessageAsync(req.Phone, req.Text, ct);
            return Results.Ok(new { ok = r.Sent, delivery = r.DeliveryStatus, error = r.Error });
        });

        // Lê o número do WhatsApp registrado no emulador (registration_jid) — auto-preenche o Passo 2.
        group.MapGet("/whatsapp-number", async (IPhoneOrchestrator phone, CancellationToken ct) =>
            Results.Ok(new { number = await phone.GetWhatsAppNumberAsync(ct) }));

        // Estado da CONTA no emulador (registered/revoked/none/unknown) + se este aparelho aceita a
        // limpeza por imagem-ouro. A tela usa isso pra oferecer o botão CERTO: "Trocar chip" (pm clear)
        // numa troca por escolha, "Limpar aparelho restringido" quando o servidor derrubou a conta — ali
        // o pm clear é insuficiente (mantém android_id/GSF do device queimado). Ver GetWhatsAppAccountStateAsync.
        group.MapGet("/whatsapp-state", async (IPhoneOrchestrator phone, CancellationToken ct) =>
        {
            var st = await phone.GetWhatsAppAccountStateAsync(ct);
            return Results.Ok(new
            {
                state = st.State,
                phone = st.Phone,
                revokedByServer = st.RevokedByServer,
                cleanSupported = await phone.IsComposeManagedAsync(ct),
            });
        });

        // "Limpar aparelho restringido": aparelho NOVO a partir da imagem-ouro (molde que nunca registrou
        // número). Não recria nada aqui — grava o flag-volume e o emulator-watchdog executa pelo compose
        // (ver RequestCleanDeviceAsync). TRAVA invertida em relação ao /reset-emulator: aqui só vale pro
        // emulador COMPOSE-MANAGED (o A), que é quem tem watchdog + molde. Nos stacks docker-run o flag
        // ficaria sem ninguém pra consumir, então bloqueia com mensagem em vez de fingir que funcionou.
        group.MapPost("/clean-device", async (IPhoneOrchestrator phone, ISystemStateRepository stateRepo,
            IUnitOfWork uow, CancellationToken ct) =>
        {
            if (!await phone.IsComposeManagedAsync(ct))
            {
                return Results.Ok(new
                {
                    blocked = true,
                    paused = false,
                    message = "Este emulador não é gerenciado por compose — a limpeza por imagem-ouro "
                        + "existe só no stack A. Para um número novo aqui, use \"Trocar chip\".",
                });
            }

            // PAUSA A FILA ANTES de pedir o aparelho novo. Sem isto o dispatcher segue disparando contra
            // um container que está sendo APAGADO: os envios falham no adb, os contatos ficam marcados
            // como tentados e o audit enche de erro — e depois do wipe não há chip nenhum, então TODOS
            // falhariam até alguém perceber. Tem que ser ManualPauseReason: `IsManuallyPaused` (o que o
            // DispatchEngine consulta) compara com ESSA constante — pausar com um motivo qualquer não
            // pararia nada. Fica pausado de propósito: o operador retoma pelo botão do Disparo depois de
            // registrar o chip novo, reconceder as permissões e re-importar os grupos.
            var st = await stateRepo.GetAsync(ct);
            st.Pause(SystemStateAggregate.ManualPauseReason);
            await stateRepo.UpdateAsync(st, ct);
            await uow.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                blocked = false,
                paused = true,
                message = await phone.RequestCleanDeviceAsync(ct),
            });
        });

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
