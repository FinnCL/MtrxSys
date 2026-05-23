using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

internal sealed class AuthStatusCommand(
    IWahaClient waha,
    IOptions<DispatchOptions> opts,
    CancellationTokenProvider cancellation) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var sessionId = opts.Value.SessionId;
        var status = await waha.GetSessionStatusAsync(sessionId, cancellation.Token);

        var color = status switch
        {
            WahaSessionStatus.Working => "green",
            WahaSessionStatus.ScanQrCode => "yellow",
            WahaSessionStatus.Starting => "blue",
            WahaSessionStatus.Failed or WahaSessionStatus.Stopped => "red",
            _ => "grey",
        };
        AnsiConsole.MarkupLine($"Session [bold]{sessionId}[/] => [{color}]{status}[/]");
        return 0;
    }
}
