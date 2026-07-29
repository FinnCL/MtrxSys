using System.ComponentModel;
using MtrxSys.Cli.Infrastructure;
using MtrxSys.Core.Application.Abstractions;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MtrxSys.Cli.Commands;

/// <summary>Runner ENXUTO do piloto do aparelho físico: dispara pela UI do aparelho, sem banco, sem
/// fila, sem WAHA. Responde "o físico entrega?" sem subir Postgres nem mexer no deploy.</summary>
/// <remarks>
/// <para>Não substitui o DispatchEngine e não quer substituir: aqui não há teto de aquecimento, ledger,
/// opt-out, gate por chip nem auditoria. É bancada de teste. Por isso o teto de mensagens por execução
/// é baixo e o intervalo padrão é o mesmo do disparo real (150-360s).</para>
/// <para>Usa o <see cref="IPhoneOrchestrator"/> resolvido pelo DI, então respeita o `Phone__Engine`:
/// com "physical" dirige o celular no cabo; com o default dirige o emulador.</para>
/// </remarks>
internal sealed class PhoneSendCommand(
    IPhoneOrchestrator phone,
    CancellationTokenProvider cancellation) : AsyncCommand<PhoneSendCommand.Settings>
{
    internal sealed class Settings : CommandSettings
    {
        [CommandOption("--to <TELEFONE>")]
        [Description("Número em E.164 só com dígitos (ex.: 5511987654321). Repita para vários.")]
        public string[] To { get; init; } = [];

        [CommandOption("--text <TEXTO>")]
        [Description("Texto a enviar. Curto e sem link no piloto (ex.: \"oi\").")]
        public string Text { get; init; } = "oi";

        [CommandOption("--save-contact")]
        [Description("Grava o número na agenda do aparelho antes de enviar.")]
        public bool SaveContact { get; init; }

        [CommandOption("--min-delay <SEGUNDOS>")]
        [Description("Intervalo mínimo entre envios (default 150).")]
        public int MinDelaySeconds { get; init; } = 150;

        [CommandOption("--max-delay <SEGUNDOS>")]
        [Description("Intervalo máximo entre envios (default 360).")]
        public int MaxDelaySeconds { get; init; } = 360;

        [CommandOption("--dry-run")]
        [Description("Só mostra o que faria; não toca no aparelho.")]
        public bool DryRun { get; init; }

        public override Spectre.Console.ValidationResult Validate()
        {
            if (To.Length == 0)
            {
                return Spectre.Console.ValidationResult.Error("informe ao menos um --to.");
            }
            // Teto BAIXO de propósito: isto é bancada, não produção. Uma rajada de um chip novo é o
            // gatilho de ban que este projeto inteiro tenta evitar.
            if (To.Length > 10)
            {
                return Spectre.Console.ValidationResult.Error(
                    "no máximo 10 números por execução — este runner é bancada de teste, não disparo.");
            }
            if (string.IsNullOrWhiteSpace(Text))
            {
                return Spectre.Console.ValidationResult.Error("--text vazio.");
            }
            if (MinDelaySeconds < 0 || MaxDelaySeconds < MinDelaySeconds)
            {
                return Spectre.Console.ValidationResult.Error("intervalo inválido (max precisa ser >= min).");
            }
            return Spectre.Console.ValidationResult.Success();
        }
    }

    public override async Task<int> ExecuteAsync(CommandContext context, Settings s)
    {
        var ct = cancellation.Token;

        var status = await phone.GetStatusAsync(ct);
        AnsiConsole.MarkupLine($"Aparelho: [bold]{status.State}[/] (running={status.Running})");
        if (!status.Running)
        {
            AnsiConsole.MarkupLine(status.State switch
            {
                // Cada estado tem um conserto diferente, e mandar conferir o cabo quando o cabo está bom
                // é o jeito mais rápido de perder meia hora.
                "unauthorized" =>
                    "[red]Aparelho não autorizado.[/] Aceite o diálogo \"Permitir depuração USB?\" na tela "
                    + "do celular e marque \"Sempre permitir\". Se não aparecer, desconecte e reconecte o cabo.",
                _ =>
                    "[red]Aparelho indisponível.[/] Confira: cabo de DADOS (não só carga), Depuração USB "
                    + "ligada, tela desbloqueada, Phone__AdbSerial e Phone__AdbPath.",
            });
            return 1;
        }
        if (!await phone.IsBootedAsync(ct))
        {
            AnsiConsole.MarkupLine("[red]Android não respondeu ao getprop.[/]");
            return 1;
        }

        // Pré-voo da DIGITAÇÃO. Sem isto, texto com acento ou emoji falha em TODOS os envios, um a um,
        // depois de abrir conversa e tocar na tela em cada contato. Melhor descobrir aqui.
        if (await phone.CheckTypingCapabilityAsync(s.Text, ct) is { } motivo)
        {
            AnsiConsole.MarkupLine($"[red]Não dá pra digitar este texto neste aparelho:[/] {motivo.EscapeMarkup()}");
            return 1;
        }

        var falhas = 0;
        for (var i = 0; i < s.To.Length; i++)
        {
            var to = new string([.. s.To[i].Where(char.IsDigit)]);
            if (to.Length is < 12 or > 13)
            {
                // 12-13 dígitos = 55 + DDD + número (legado sem o 9º dígito dá 12). Fora dessa faixa é
                // DDD faltando ou dígito sobrando, e número malformado é o vetor do 463 — o mesmo erro
                // que quase mandou "55921404487" (11 dígitos, sem DDD) em 2026-07-29.
                AnsiConsole.MarkupLine(
                    $"[red]{to}[/]: {to.Length} dígitos, esperado 12 ou 13 (55 + DDD + número). Pulado.");
                falhas++;
                continue;
            }

            if (s.DryRun)
            {
                AnsiConsole.MarkupLine($"[grey]dry-run[/] enviaria para [bold]{to}[/]: \"{s.Text}\"");
                continue;
            }

            if (s.SaveContact)
            {
                var saved = await phone.SaveContactAsync(to, null, ct);
                AnsiConsole.MarkupLine($"[grey]agenda[/] {to}: {saved}");
            }

            var r = await phone.SendWhatsAppMessageAsync(to, s.Text, ct);
            if (r.Sent)
            {
                AnsiConsole.MarkupLine($"[green]enviado[/] {to} (entrega: {r.DeliveryStatus ?? "?"})");
            }
            else
            {
                falhas++;
                AnsiConsole.MarkupLine($"[red]falhou[/] {to}: {r.Error}");
            }

            if (i < s.To.Length - 1)
            {
                var wait = Random.Shared.Next(s.MinDelaySeconds, s.MaxDelaySeconds + 1);
                AnsiConsole.MarkupLine($"[grey]aguardando {wait}s antes do próximo…[/]");
                await Task.Delay(TimeSpan.FromSeconds(wait), ct);
            }
        }

        AnsiConsole.MarkupLine(falhas == 0 ? "[green]concluído sem falhas[/]" : $"[yellow]concluído com {falhas} falha(s)[/]");
        return falhas == 0 ? 0 : 1;
    }
}
