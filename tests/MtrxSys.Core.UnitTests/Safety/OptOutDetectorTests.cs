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
    public void Ignores_non_optout(string body)
    {
        OptOutDetector.IsOptOut(body).Should().BeFalse($"'{body}' não deveria ser opt-out");
    }
}
