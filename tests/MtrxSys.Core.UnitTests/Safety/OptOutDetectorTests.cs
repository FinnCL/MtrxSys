using FluentAssertions;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class OptOutDetectorTests
{
    [Theory]
    // maiúsculas / minúsculas / capitalizado
    [InlineData("SAIR")]
    [InlineData("sair")]
    [InlineData("Sair")]
    [InlineData("SaIr")]
    // pontuação e espaços
    [InlineData("  SAIR!  ")]
    [InlineData("sair.")]
    [InlineData("SAIR 🙏")]
    // comando em resposta curta
    [InlineData("sair por favor")]
    [InlineData("quero parar")]
    [InlineData("pode cancelar")]
    // acento e frases
    [InlineData("não quero receber mais mensagens")]
    [InlineData("para de mandar isso")]
    [InlineData("me tira da lista por favor")]
    // ñ (U+00F1) normaliza pra n
    [InlineData("mañana cancelar")]
    // Acento DECOMPOSTO (NFD): "n" + "a" + U+0303 (til combinante) + "o quero receber". O projeto
    // roda InvariantGlobalization=true, onde string.Normalize é no-op; a marca combinante precisa
    // cair pela categoria Unicode (NonSpacingMark), senão viraria "na o" e não casaria a frase.
    [InlineData("não quero receber")]
    public void Detects_optout(string body)
    {
        OptOutDetector.IsOptOut(body).Should().BeTrue($"'{body}' deveria ser opt-out");
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Oi, tudo bem?")]
    [InlineData("Tenho interesse, me conta mais")]
    // palavra ambígua dentro de frase longa não dispara
    [InlineData("vou sair mais tarde de casa hoje")]
    // "para" é preposição: respostas curtas de lead interessado NÃO podem virar opt-out
    [InlineData("é para hoje?")]
    [InlineData("para qual cidade")]
    [InlineData("manda para mim")]
    [InlineData("ok para mim")]
    [InlineData("tem para entrega")]
    public void Ignores_non_optout(string body)
    {
        OptOutDetector.IsOptOut(body).Should().BeFalse($"'{body}' não deveria ser opt-out");
    }
}
