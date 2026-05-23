using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

internal sealed class GroupsListCommand(
    IWahaClient waha,
    IOptions<DispatchOptions> opts,
    CancellationTokenProvider cancellation) : AsyncCommand
{
    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        var groups = await waha.ListGroupsAsync(opts.Value.SessionId, cancellation.Token);
        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Nenhum grupo encontrado nesta sessão.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Nome")
            .AddColumn(new TableColumn("Participantes").RightAligned());

        foreach (var g in groups)
        {
            table.AddRow(g.Id, Markup.Escape(g.Name), g.ParticipantsCount?.ToString() ?? "-");
        }
        AnsiConsole.Write(table);
        return 0;
    }
}
