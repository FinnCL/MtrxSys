using FluentAssertions;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.UnitTests.Safety;

/// <summary>A política de sequência de falhas do lote de envio pelo aparelho.</summary>
/// <remarks>
/// Esta lógica morava dentro do laço do console, em variáveis soltas, onde não tinha como ser testada
/// (o projeto do CLI não tem testes). Saiu de lá para poder ter estes testes, e o primeiro que eles
/// pegaram foi um erro de ANÁLISE do autor, que supunha que contadores separados deixariam uma
/// sequência alternada correr para sempre. Não deixavam.
/// </remarks>
public sealed class BatchStopPolicyTests
{
    // O padrão do console depois de 2026-08-07: nenhuma falha interrompe o lote.
    private static BatchStopPolicy Padrao() => new(0);

    // 🔴 O CASO QUE DECIDIU O PADRÃO. Medido em 2026-08-07: três números na forma errada, seguidos,
    // derrubaram um lote de 30 em 22/30 com o aparelho perfeito, e 15 contatos bons ficaram sem
    // receber. Se falhou, nada saiu: seguir para o próximo não custa entrega nenhuma.
    [Fact]
    public void Falha_nunca_interrompe_o_lote_por_padrao()
    {
        var d = Padrao();
        for (var i = 0; i < 50; i++)
        {
            d.NoAccount();
            d.DeviceFailure();
            d.ShouldStop.Should().BeFalse("mostrar a falha e passar para o próximo é o comportamento pedido");
        }
    }

    [Fact]
    public void Quem_pede_um_teto_recebe_o_teto()
    {
        var d = new BatchStopPolicy(3);
        d.NoAccount();
        d.DeviceFailure();
        d.ShouldStop.Should().BeFalse();

        d.NoAccount();
        d.ShouldStop.Should().BeTrue("o teto conta QUALQUER falha, não a categoria");
    }

    [Fact]
    public void Uma_entrega_no_meio_zera_tudo()
    {
        var d = new BatchStopPolicy(3);
        d.DeviceFailure();
        d.DeviceFailure();
        d.NoAccount();

        d.Delivered();

        d.ConsecutiveFailures.Should().Be(0);
        d.ConsecutiveDeviceFailures.Should().Be(0);
        d.ConsecutiveNoAccount.Should().Be(0);
        d.ShouldStop.Should().BeFalse("entrega prova que o aparelho está bom E que a lista não é toda lixo");
    }

    // O aviso é o que sobrou no lugar da parada: falha de aparelho é a única que prevê o próximo
    // contato, porque tela bloqueada continua bloqueada.
    [Fact]
    public void Grita_uma_vez_quando_o_aparelho_passa_a_ser_o_suspeito()
    {
        var d = Padrao();
        d.DeviceFailure();
        d.DeviceFailure();
        d.AcabouDeAcusarAparelho.Should().BeFalse("duas ainda podem ser azar");

        d.DeviceFailure();
        d.AcabouDeAcusarAparelho.Should().BeTrue();

        d.DeviceFailure();
        d.AcabouDeAcusarAparelho.Should().BeFalse(
            "repetir o alerta a cada contato vira ruído, e a pessoa aprende a pular a tela inteira");
    }

    // A suposição errada do autor, virada teste: número morto intercalado NÃO apaga o rastro do
    // aparelho. Se alguém um dia zerar um contador com o outro, o alerta de celular travado some
    // exatamente na lista em que ele é mais necessário.
    [Fact]
    public void Numero_morto_no_meio_nao_apaga_o_rastro_do_aparelho()
    {
        var d = Padrao();
        d.DeviceFailure();
        d.NoAccount();
        d.DeviceFailure();
        d.NoAccount();
        d.DeviceFailure();

        d.ConsecutiveDeviceFailures.Should().Be(3);
        d.AcabouDeAcusarAparelho.Should().BeTrue();
        d.ConsecutiveNoAccount.Should().Be(2);
        d.ConsecutiveFailures.Should().Be(5);
    }

    [Fact]
    public void Sequencia_e_o_que_muda_o_ritmo_nao_a_categoria()
    {
        var d = Padrao();
        d.NoAccount();
        d.InFailureStreak.Should().BeFalse("falha isolada segue rápida: nada saiu, e esperar seria pagar "
            + "anti-ban por mensagem que não existiu");

        d.NoAccount();
        d.NoAccount();
        d.InFailureStreak.Should().BeTrue(
            "o que pesa contra o chip é a RAJADA de conversas abertas, e com o lote seguindo em frente "
            + "o ritmo é a única proteção que sobra");

        d.Delivered();
        d.InFailureStreak.Should().BeFalse();
    }

    [Fact]
    public void Teto_negativo_e_tratado_como_sem_teto()
    {
        var d = new BatchStopPolicy(-5);
        d.DeviceFailure();
        d.DeviceFailure();
        d.DeviceFailure();
        d.ShouldStop.Should().BeFalse();
    }
}
