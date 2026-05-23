using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

internal sealed class GroupsJoinCommand(
    IWahaClient waha,
    IOptions<DispatchOptions> opts,
    CancellationTokenProvider cancellation) : AsyncCommand<GroupsJoinCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<invite-link-or-code>")]
        public string Invite { get; init; } = string.Empty;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var group = await waha.JoinGroupByInviteAsync(opts.Value.SessionId, settings.Invite, cancellation.Token);
        AnsiConsole.MarkupLine($"[green]Entrou no grupo:[/] [bold]{Markup.Escape(group.Name)}[/] (id={group.Id})");
        return 0;
    }
}
