using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Reporting;

namespace MtrxSys.Core.UnitTests.Reporting;

public sealed class DiagnosticoDeFalhaTests
{
    [Theory]
    [InlineData(FalhaCausa.Nenhuma, GrupoDaFalha.Nenhum)]
    [InlineData(FalhaCausa.NumeroSemConta, GrupoDaFalha.Numero)]
    [InlineData(FalhaCausa.ContaRestringida, GrupoDaFalha.Chip)]
    [InlineData(FalhaCausa.ToqueNaoConfirmado, GrupoDaFalha.Incerto)]
    [InlineData(FalhaCausa.Timeout, GrupoDaFalha.Lentidao)]
    [InlineData(FalhaCausa.DigitacaoFalhou, GrupoDaFalha.Configuracao)]
    [InlineData(FalhaCausa.EntradaInvalida, GrupoDaFalha.Configuracao)]
    [InlineData(FalhaCausa.TelaBloqueada, GrupoDaFalha.Aparelho)]
    [InlineData(FalhaCausa.ConversaNaoAbriu, GrupoDaFalha.Aparelho)]
    [InlineData(FalhaCausa.AdbFalhou, GrupoDaFalha.Aparelho)]
    public void Grupo_classifica_a_causa(FalhaCausa causa, GrupoDaFalha esperado) =>
        DiagnosticoDeFalha.Grupo(causa).Should().Be(esperado);

    /// <summary>Causa nova não pode nascer sem rótulo nem sem ação: o mapa é escrito à mão e é
    /// exatamente onde um valor acrescentado ao enum passaria despercebido.</summary>
    [Fact]
    public void Toda_causa_tem_rotulo_grupo_e_acao()
    {
        foreach (var causa in Enum.GetValues<FalhaCausa>().Where(c => c is not FalhaCausa.Nenhuma))
        {
            DiagnosticoDeFalha.Rotulo(causa).Should().NotBeNullOrWhiteSpace(because: $"{causa} precisa de rótulo");
            DiagnosticoDeFalha.OQueFazer(causa).Should().NotBeNullOrWhiteSpace(because: $"{causa} precisa de ação");
            DiagnosticoDeFalha.Grupo(causa).Should().NotBe(GrupoDaFalha.Nenhum, because: $"{causa} é uma falha");
            DiagnosticoDeFalha.RotuloDoGrupo(DiagnosticoDeFalha.Grupo(causa)).Should().NotBeNullOrWhiteSpace();
        }
    }

    /// <summary>O sucesso não recebe rótulo de falha, senão a coluna "o que fazer" manda o operador
    /// consertar um envio que deu certo.</summary>
    [Fact]
    public void Sucesso_nao_tem_rotulo_nem_acao()
    {
        DiagnosticoDeFalha.Rotulo(FalhaCausa.Nenhuma).Should().BeEmpty();
        DiagnosticoDeFalha.OQueFazer(FalhaCausa.Nenhuma).Should().BeEmpty();
        DiagnosticoDeFalha.RotuloDoGrupo(GrupoDaFalha.Nenhum).Should().BeEmpty();
    }

    /// <summary>🔴 A garantia que impede a lista de ser dizimada por um chip restrito ou um cabo solto.
    /// Só o veredito do app sobre o NÚMERO tira alguém da fila.</summary>
    [Fact]
    public void Só_numero_sem_conta_condena_o_contato()
    {
        DiagnosticoDeFalha.ContatoMorto(FalhaCausa.NumeroSemConta).Should().BeTrue();
        foreach (var causa in Enum.GetValues<FalhaCausa>().Where(c => c is not FalhaCausa.NumeroSemConta))
        {
            DiagnosticoDeFalha.ContatoMorto(causa).Should().BeFalse(because: $"{causa} não fala do contato");
        }
    }
}
