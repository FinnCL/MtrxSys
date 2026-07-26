using System.Text.Json;
using FluentAssertions;
using Xunit;

namespace MtrxSys.Core.UnitTests.Safety;

/// <summary>
/// Trava a paridade das TRÊS cópias da curva de aquecimento: o default em código
/// (<c>WarmupManager.DefaultCurve</c>) e o <c>Warmup:Curve</c> dos appsettings da Api e do Dispatcher.
///
/// <para>POR QUE ISTO EXISTE: a curva é o teto diário de envio, a defesa anti-ban mais direta do sistema,
/// e está escrita em três arquivos sincronizados À MÃO. A divergência é silenciosa e ASSIMÉTRICA — o
/// Dispatcher aplica o teto, a Api alimenta a UI. Divergindo, o operador lê "posso mandar 16 hoje"
/// enquanto o motor libera 5 (ou o contrário, que é o lado perigoso). Nada no build reclamava; a
/// consistência dependia de alguém lembrar de editar os três.</para>
///
/// <para>Em 2026-07-26 o comentário do <c>DefaultCurve</c> descrevia platô 400 sobre um array de platô
/// 200 — resíduo de uma curva revertida no 18133e5 por ter entrado no combo que restringiu o chip A. Um
/// leitor conferindo a "divergência" poderia ter restaurado o 400. Comentário não trava nada; teste
/// trava.</para>
/// </summary>
public sealed class WarmupCurveParityTests
{
    // A MESMA curva que WarmupManager.DefaultCurve. Repetida aqui DE PROPÓSITO: o teste tem que falhar
    // quando o código muda sozinho, e ler a constante privada por reflexão faria os dois se moverem
    // juntos — um teste que concorda com qualquer valor não testa nada. Mudança legítima da curva atualiza
    // QUATRO lugares e o teste é o lembrete de que os outros três existem.
    private static readonly int[] Expected =
        [3, 5, 8, 12, 16, 21, 27, 34, 42, 51, 62, 75, 90, 107, 125, 145, 165, 185, 200];

    [Theory]
    [InlineData("src/MtrxSys.Api/appsettings.json")]
    [InlineData("src/MtrxSys.Dispatcher/appsettings.json")]
    public void Curva_dos_appsettings_bate_com_a_do_codigo(string relativePath)
    {
        var path = Path.Combine(RepoRoot(), relativePath);
        File.Exists(path).Should().BeTrue($"o appsettings esperado não está em {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        doc.RootElement.TryGetProperty("Warmup", out var warmup)
            .Should().BeTrue($"{relativePath} não tem a seção Warmup");
        warmup.TryGetProperty("Curve", out var curve)
            .Should().BeTrue($"{relativePath} não tem Warmup:Curve");

        curve.EnumerateArray().Select(e => e.GetInt32()).Should().Equal(
            Expected,
            $"a curva de {relativePath} divergiu da do código (WarmupManager.DefaultCurve). "
            + "A curva é o teto diário anti-ban e vive em três arquivos: os três têm que ser iguais. "
            + "Se a mudança é intencional, atualize as TRÊS cópias e o Expected deste teste.");
    }

    /// <summary>Sobe até a pasta que contém o MtrxSys.slnx.</summary>
    /// <remarks>
    /// FALHA em vez de pular quando não acha. Um teste de paridade que se auto-desabilita ao rodar de um
    /// diretório inesperado é pior que nenhum: fica verde para sempre e cria a impressão de que a curva
    /// está vigiada. Ruído barulhento é preferível a sensor cego — a mesma regra do resto do projeto.
    /// </remarks>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "MtrxSys.slnx")))
        {
            dir = dir.Parent;
        }
        dir.Should().NotBeNull(
            $"não achei MtrxSys.slnx subindo de {AppContext.BaseDirectory} — este teste lê os "
            + "appsettings pelo caminho do repositório.");
        return dir!.FullName;
    }
}
