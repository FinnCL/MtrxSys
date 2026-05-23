using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

internal sealed class AuthQrCommand(
    IWahaClient waha,
    IOptions<DispatchOptions> opts,
    CancellationTokenProvider cancellation) : AsyncCommand<AuthQrCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandOption("-o|--output")]
        public string Output { get; init; } = "waha-qr.png";

        [CommandOption("--start")]
        public bool StartIfStopped { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var ct = cancellation.Token;
        var sessionId = opts.Value.SessionId;
        var status = await waha.GetSessionStatusAsync(sessionId, ct);

        if (status == WahaSessionStatus.Stopped && settings.StartIfStopped)
        {
            AnsiConsole.MarkupLine("[blue]Session stopped, starting...[/]");
            await waha.EnsureSessionStartedAsync(sessionId, ct);
            for (var i = 0; i < 30; i++)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                status = await waha.GetSessionStatusAsync(sessionId, ct);
                if (status is WahaSessionStatus.ScanQrCode or WahaSessionStatus.Working)
                {
                    break;
                }
            }
        }

        if (status != WahaSessionStatus.ScanQrCode)
        {
            AnsiConsole.MarkupLine($"[yellow]Status atual: {status}. QR só fica disponível em SCAN_QR_CODE.[/]");
            return status == WahaSessionStatus.Working ? 0 : 1;
        }

        var bytes = await waha.GetQrPngAsync(sessionId, ct);
        await File.WriteAllBytesAsync(settings.Output, bytes, ct);
        AnsiConsole.MarkupLine($"[green]QR salvo em {Path.GetFullPath(settings.Output)}[/]");
        AnsiConsole.MarkupLine("Abra a imagem e escaneie: [bold]WhatsApp -> Aparelhos conectados -> Conectar um aparelho[/].");
        AnsiConsole.MarkupLine("Rode [bold]mtrx auth status[/] depois pra confirmar que virou WORKING.");
        return 0;
    }
}
