using System.Collections;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Common;
using QRCoder;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

internal sealed class ChatCommand(
    IWahaClient waha,
    IOptions<DispatchOptions> opts,
    CancellationTokenProvider cancellation,
    ILogger<ChatCommand> log) : AsyncCommand
{
    private readonly string _sessionId = opts.Value.SessionId;
    private readonly CancellationToken _ct = cancellation.Token;

    public override async Task<int> ExecuteAsync(CommandContext context)
    {
        try
        {
            if (!await EnsureLoggedInAsync(_ct))
            {
                return 1;
            }

            AnsiConsole.Clear();
            ShowWelcome();

            while (!_ct.IsCancellationRequested)
            {
                var choice = AnsiConsole.Prompt(
                    new SelectionPrompt<string>()
                        .Title("[bold]Menu principal[/]")
                        .PageSize(10)
                        .AddChoices(
                            "Listar conversas",
                            "Abrir conversa",
                            "Mandar mensagem",
                            "Listar grupos",
                            "Status da sessão",
                            "Sair"));

                try
                {
                    switch (choice)
                    {
                        case "Listar conversas": await ListChatsAsync(_ct); break;
                        case "Abrir conversa": await OpenChatAsync(_ct); break;
                        case "Mandar mensagem": await SendMessageAsync(_ct); break;
                        case "Listar grupos": await ListGroupsAsync(_ct); break;
                        case "Status da sessão": await ShowStatusAsync(_ct); break;
                        case "Sair":
                            AnsiConsole.MarkupLine("[grey]Até logo.[/]");
                            return 0;
                    }
                }
                catch (OperationCanceledException) when (_ct.IsCancellationRequested)
                {
                    AnsiConsole.MarkupLine("[grey]Cancelado.[/]");
                    return 0;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                    log.LogError(ex, "Falha ao executar opção {Choice}", choice);
                    AnsiConsole.MarkupLine($"[red]Erro:[/] {Markup.Escape(ex.Message)}");
                    AnsiConsole.MarkupLine("[grey](detalhes completos no log)[/]");
                }
#pragma warning restore CA1031

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]Pressione qualquer tecla para voltar ao menu... (Ctrl+C pra sair)[/]");
                Console.ReadKey(intercept: true);
                AnsiConsole.Clear();
            }
            return 0;
        }
        catch (OperationCanceledException) when (_ct.IsCancellationRequested)
        {
            AnsiConsole.MarkupLine("[grey]Encerrado.[/]");
            return 0;
        }
    }

    private async Task<bool> EnsureLoggedInAsync(CancellationToken ct)
    {
        var status = await waha.GetSessionStatusAsync(_sessionId, ct);
        if (status == WahaSessionStatus.Stopped)
        {
            AnsiConsole.MarkupLine("[blue]Iniciando sessão WAHA...[/]");
            await waha.EnsureSessionStartedAsync(_sessionId, ct);
            status = await PollUntilAsync(s => s != WahaSessionStatus.Stopped, ct);
        }

        if (status == WahaSessionStatus.Working)
        {
            return true;
        }

        string? lastQr = null;
        while (status != WahaSessionStatus.Working)
        {
            ct.ThrowIfCancellationRequested();

            if (status == WahaSessionStatus.Failed)
            {
                AnsiConsole.MarkupLine("[red]Sessão em estado FAILED. Reinicie o container WAHA.[/]");
                return false;
            }

            if (status == WahaSessionStatus.ScanQrCode)
            {
                var raw = await SafeGetQrAsync(ct);
                if (!string.IsNullOrWhiteSpace(raw) && raw != lastQr)
                {
                    lastQr = raw;
                    AnsiConsole.Clear();
                    AnsiConsole.MarkupLine("[bold yellow]Escaneie o QR com seu WhatsApp[/]");
                    AnsiConsole.MarkupLine("[grey]WhatsApp -> Aparelhos conectados -> Conectar um aparelho[/]");
                    AnsiConsole.WriteLine();
                    RenderAsciiQr(raw);
                    AnsiConsole.WriteLine();
                    AnsiConsole.MarkupLine("[grey]Aguardando scan... (o QR rotaciona a cada ~20s, Ctrl+C pra cancelar)[/]");
                }
            }
            else if (status == WahaSessionStatus.Starting)
            {
                AnsiConsole.MarkupLine("[blue]Conectando... aguarde.[/]");
            }

            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            status = await waha.GetSessionStatusAsync(_sessionId, ct);
        }

        AnsiConsole.Clear();
        AnsiConsole.MarkupLine("[green bold]Conectado![/]");
        await Task.Delay(800, ct);
        return true;
    }

    private async Task<string?> SafeGetQrAsync(CancellationToken ct)
    {
        try
        {
            return await waha.GetQrRawAsync(_sessionId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            log.LogWarning(ex, "Falha buscando QR da sessão {SessionId}; vou tentar de novo no próximo poll", _sessionId);
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task<WahaSessionStatus> PollUntilAsync(Func<WahaSessionStatus, bool> predicate, CancellationToken ct)
    {
        for (var i = 0; i < 20; i++)
        {
            await Task.Delay(1000, ct);
            var s = await waha.GetSessionStatusAsync(_sessionId, ct);
            if (predicate(s))
            {
                return s;
            }
        }
        return await waha.GetSessionStatusAsync(_sessionId, ct);
    }

    private static readonly char[] QuadrantGlyphs =
    [
        ' ', '▘', '▝', '▀',
        '▖', '▌', '▞', '▛',
        '▗', '▚', '▐', '▜',
        '▄', '▙', '▟', '█',
    ];

    private static void RenderAsciiQr(string content)
    {
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(content, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        var size = matrix.Count;
        const int quiet = 2;
        var lo = -quiet;
        var hi = size + quiet;

        var sb = new StringBuilder();
        for (var y = lo; y < hi; y += 2)
        {
            for (var x = lo; x < hi; x += 2)
            {
                var bits = 0;
                if (IsDark(matrix, x + 0, y + 0, size)) bits |= 0x1;
                if (IsDark(matrix, x + 1, y + 0, size)) bits |= 0x2;
                if (IsDark(matrix, x + 0, y + 1, size)) bits |= 0x4;
                if (IsDark(matrix, x + 1, y + 1, size)) bits |= 0x8;
                sb.Append(QuadrantGlyphs[bits]);
            }
            sb.AppendLine();
        }
        Console.Write(sb.ToString());
    }

    private static bool IsDark(List<BitArray> matrix, int x, int y, int size)
    {
        if (x < 0 || x >= size || y < 0 || y >= size)
        {
            return false;
        }
        return matrix[y][x];
    }

    private void ShowWelcome()
    {
        var panel = new Panel(
            "[green]Sessão conectada[/]\n" +
            $"Session: [bold]{_sessionId}[/]\n" +
            "Use as setas para navegar no menu.")
            .Header("[bold]MtrxSys WhatsApp Console[/]")
            .Border(BoxBorder.Rounded);
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    private async Task ListChatsAsync(CancellationToken ct)
    {
        var chats = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Carregando conversas...", _ =>
                waha.ListChatsOverviewAsync(_sessionId, limit: 500, ct));

        if (chats.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Sem conversas.[/]");
            return;
        }

        AnsiConsole.MarkupLine($"[grey]Total: {chats.Count} conversas[/]");
        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("Tipo")
            .AddColumn("Nome")
            .AddColumn("Última mensagem")
            .AddColumn("Quando");

        foreach (var c in chats)
        {
            table.AddRow(
                c.IsGroup ? "[blue]grupo[/]" : "[green]pessoa[/]",
                Markup.Escape(c.Name.TruncateWithEllipsis(30)),
                Markup.Escape((c.LastMessagePreview ?? "-").TruncateWithEllipsis(50)),
                c.LastMessageAt?.ToLocalTime().ToString("dd/MM HH:mm") ?? "-");
        }
        AnsiConsole.Write(table);
    }

    private async Task OpenChatAsync(CancellationToken ct)
    {
        var chats = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Carregando conversas...", _ =>
                waha.ListChatsOverviewAsync(_sessionId, limit: 500, ct));

        if (chats.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Sem conversas.[/]");
            return;
        }

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<WahaChat>()
                .Title($"Escolha uma conversa ([grey]{chats.Count} total — digite pra buscar[/])")
                .PageSize(25)
                .MoreChoicesText("[grey](use ↑↓ pra mais)[/]")
                .EnableSearch()
                .UseConverter(c => $"{(c.IsGroup ? "[blue][grupo][/]" : "[green][pessoa][/]")} {Markup.Escape(c.Name.TruncateWithEllipsis(50))}")
                .AddChoices(chats));

        var messages = await waha.GetChatMessagesAsync(_sessionId, selected.Id, limit: 30, ct);
        AnsiConsole.Clear();
        AnsiConsole.Write(new Rule($"[bold]{Markup.Escape(selected.Name)}[/]").LeftJustified());

        foreach (var m in messages)
        {
            var who = m.FromMe ? "[green]Você[/]" : $"[blue]{Markup.Escape(m.Author.TruncateWithEllipsis(20))}[/]";
            var when = m.Timestamp.ToLocalTime().ToString("HH:mm");
            AnsiConsole.MarkupLine($"[grey]{when}[/]  {who}: {Markup.Escape(m.Body)}");
        }

        AnsiConsole.Write(new Rule().LeftJustified());

        if (AnsiConsole.Confirm("Mandar uma mensagem nessa conversa?", defaultValue: false))
        {
            var text = AnsiConsole.Ask<string>("Mensagem:");
            await waha.SendTextAsync(_sessionId, selected.Id, text, ct);
            AnsiConsole.MarkupLine("[green]Enviada.[/]");
        }
    }

    private async Task SendMessageAsync(CancellationToken ct)
    {
        var phone = AnsiConsole.Ask<string>("Número destino (E.164, ex.: +5511999999999):");
        var text = AnsiConsole.Ask<string>("Mensagem:");
        var id = await waha.SendTextAsync(_sessionId, phone, text, ct);
        AnsiConsole.MarkupLine($"[green]Enviada.[/] id=[grey]{Markup.Escape(id)}[/]");
    }

    private async Task ListGroupsAsync(CancellationToken ct)
    {
        var groups = await waha.ListGroupsAsync(_sessionId, ct);
        if (groups.Count == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Você não está em nenhum grupo.[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded)
            .AddColumn("ID")
            .AddColumn("Nome")
            .AddColumn(new TableColumn("Participantes").RightAligned());

        foreach (var g in groups)
        {
            table.AddRow(
                Markup.Escape(g.Id.TruncateWithEllipsis(40)),
                Markup.Escape(g.Name.TruncateWithEllipsis(40)),
                g.ParticipantsCount?.ToString() ?? "-");
        }
        AnsiConsole.Write(table);
    }

    private async Task ShowStatusAsync(CancellationToken ct)
    {
        var s = await waha.GetSessionStatusAsync(_sessionId, ct);
        AnsiConsole.MarkupLine($"Session [bold]{_sessionId}[/] => [yellow]{s}[/]");
    }

}
