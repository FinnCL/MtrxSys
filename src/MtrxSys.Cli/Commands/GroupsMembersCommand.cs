using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.UseCases.Contacts;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

internal sealed class GroupsMembersCommand(
    ImportGroupMembersUseCase useCase,
    CancellationTokenProvider cancellation) : AsyncCommand<GroupsMembersCommand.Settings>
{
    public sealed class Settings : CommandSettings
    {
        [CommandArgument(0, "<group-id>")]
        public string GroupId { get; init; } = string.Empty;

        [CommandOption("--tag")]
        public string? Tag { get; init; }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings settings)
    {
        var result = await useCase.ExecuteAsync(settings.GroupId, settings.Tag, cancellation.Token);

        var panel = new Panel(
            $"Total no grupo: [bold]{result.Total}[/]\n" +
            $"Novos importados: [green]{result.Imported}[/]\n" +
            $"Já existiam: [yellow]{result.Duplicated}[/]\n" +
            $"Falharam: [red]{result.Failed}[/]")
            .Header("Import concluído")
            .Border(BoxBorder.Rounded);

        AnsiConsole.Write(panel);

        if (result.Failed > 0)
        {
            AnsiConsole.MarkupLine("[red]Falhas:[/]");
            foreach (var f in result.Failures.Take(20))
            {
                AnsiConsole.MarkupLine($"  • [grey]{Markup.Escape(f.Phone)}[/] — {Markup.Escape(f.Reason)}");
            }
            if (result.Failures.Count > 20)
            {
                AnsiConsole.MarkupLine($"  [grey]... e mais {result.Failures.Count - 20} falhas[/]");
            }
        }

        return result.Failed > 0 ? 2 : 0;
    }
}
