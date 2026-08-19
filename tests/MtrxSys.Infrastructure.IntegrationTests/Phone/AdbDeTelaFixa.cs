using System.Globalization;
using System.Text.RegularExpressions;
using MtrxSys.Infrastructure.Phone;

namespace MtrxSys.Infrastructure.IntegrationTests.Phone;

/// <summary>Um Android de mentira que mostra SEMPRE a mesma tela, aconteça o que acontecer.</summary>
/// <remarks>
/// <para>É o falso certo pra afirmar sobre a LEITURA de uma tela: qual veredito ela produz, e onde o
/// driver toca (ou não toca) diante dela. Como a tela nunca muda, o teste não precisa modelar o app
/// inteiro pra fixar uma decisão que depende só do que está na árvore.</para>
/// <para>Quando o que importa é o QUE SAIU DO APARELHO ao longo de um envio (a mensagem entregue, em
/// qual conversa, com qual rascunho), o falso é o <see cref="AndroidFalso"/>, que é máquina de estados.
/// Este aqui não sabe navegar, e usá-lo pra isso provaria uma ficção.</para>
/// <para>⚠️ Tela que não muda também significa BACK que não faz nada, ou seja, um aparelho travado. Não
/// é o comportamento normal de um Android, e é de propósito: quem depende de a tela responder ao toque
/// tem que usar o outro falso.</para>
/// </remarks>
internal sealed class AdbDeTelaFixa(string tela) : IAdbRunner
{
    public List<string> Comandos { get; } = [];

    public Task<(int Code, string Out, string Err)> ShellAsync(string command, CancellationToken ct)
    {
        Comandos.Add(command);
        // `rm -f`, dump e `cat` chegam numa linha só (ver DumpUiAsync).
        return Task.FromResult(
            command.Contains("uiautomator dump", StringComparison.Ordinal)
                ? (0, $"<hierarchy>{tela}</hierarchy>", "")
                : (0, "ok", ""));
    }

    public Task<(int Code, string Out, string Err)> RawAsync(CancellationToken ct, params string[] args) =>
        Task.FromResult((0, "", ""));

    public bool SupportsRoot => false;

    public string Target => "falso";

    /// <summary>Houve algum toque DENTRO deste retângulo?</summary>
    /// <remarks>Existe pra o teste afirmar sobre ONDE o driver tocou, que é a única forma de provar que
    /// ele NÃO tocou em algo perigoso: um toque proibido não deixa outro rastro.</remarks>
    public bool TocouEm(int x1, int y1, int x2, int y2) =>
        Comandos.Select(c => TapRx.Match(c))
                .Where(m => m.Success)
                .Select(m => (
                    X: int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                    Y: int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture)))
                .Any(p => p.X >= x1 && p.X <= x2 && p.Y >= y1 && p.Y <= y2);

    private static readonly Regex TapRx = new(@"^input tap (\d+) (\d+)", RegexOptions.Compiled);
}
