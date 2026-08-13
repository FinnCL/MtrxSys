using FluentAssertions;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.UnitTests.Safety;

public sealed class FaxinaDaListaTests
{
    [Theory]
    // Lista fria normal: alguns números mortos no meio de muitos bons.
    [InlineData(100, 0)]
    [InlineData(100, 12)]
    [InlineData(50, 20)]
    // Empate libera: metade morta ainda é plausível numa lista comprada.
    [InlineData(10, 5)]
    [InlineData(4, 2)]
    public void Libera_a_faxina_quando_a_recusa_e_minoria(int tentativas, int semConta) =>
        FaxinaDaLista.PodeSuspender(tentativas, semConta).Should().BeTrue();

    /// <summary>🔴 O CASO QUE ESTA CLASSE EXISTE PRA PEGAR: chip sob restrição silenciosa faz o app
    /// negar TODO mundo, e sem a guarda a lista inteira seria apagada por um problema que não é dela.</summary>
    [Theory]
    [InlineData(87, 87)]
    [InlineData(100, 51)]
    [InlineData(10, 6)]
    [InlineData(4, 3)]
    public void Segura_a_faxina_quando_a_recusa_domina(int tentativas, int semConta) =>
        FaxinaDaLista.PodeSuspender(tentativas, semConta).Should().BeFalse();

    /// <summary>Lote curto demais não conclui nada: 2 de 3 é ruído, não padrão. Aqui a faxina passa
    /// porque a decisão volta a ser sobre cada número, e o veredito do app é o que há.</summary>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(3, 3)]
    public void Lote_curto_demais_nao_dispara_a_guarda(int tentativas, int semConta) =>
        FaxinaDaLista.PodeSuspender(tentativas, semConta).Should().BeTrue();

    [Fact]
    public void Lote_vazio_nao_estoura()
    {
        FaxinaDaLista.PodeSuspender(0, 0).Should().BeTrue();
        FaxinaDaLista.MotivoDaRecusa(0, 0).Should().BeEmpty();
    }

    /// <summary>O motivo só existe quando há recusa: texto explicando uma decisão que não foi tomada
    /// apareceria na tela do lote saudável.</summary>
    [Fact]
    public void Motivo_sai_vazio_quando_a_faxina_foi_liberada()
    {
        FaxinaDaLista.MotivoDaRecusa(100, 10).Should().BeEmpty();
        FaxinaDaLista.MotivoDaRecusa(87, 87).Should().Contain("87 de 87");
    }
}
