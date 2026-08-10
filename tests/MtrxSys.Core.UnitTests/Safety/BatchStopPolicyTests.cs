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

    // 🔴 O contador de recusados EXISTIA e ninguém o lia: uma lista inteira sendo negada rodava até o
    // fim sem alarme. Aconteceu operando em 2026-08-10, e só a desconfiança do operador parou o lote.
    [Fact]
    public void Tres_numeros_negados_seguidos_alertam()
    {
        var d = Padrao();
        d.NoAccount();
        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeFalse("dois pode ser coincidência de lista fria");

        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeTrue("três seguidos preveem uma causa comum, não três acasos");
    }

    // 🔴 REPETE, ao contrário do alerta de aparelho, e a diferença é deliberada. Com a conta
    // restringida, um único aviso no 3º contato deixaria os 84 seguintes em SILÊNCIO: quem chegasse na
    // frente da tela no meio do lote não veria nada e concluiria que estava tudo bem.
    [Fact]
    public void O_alerta_volta_espacado_enquanto_a_sequencia_durar()
    {
        var d = Padrao();
        for (var i = 0; i < 3; i++)
        {
            d.NoAccount();
        }
        d.DeveAlertarRecusas.Should().BeTrue("primeiro alerta no limiar");

        for (var i = 4; i <= 12; i++)
        {
            d.NoAccount();
            d.DeveAlertarRecusas.Should().BeFalse(
                $"na {i}ª recusa ainda é cedo: alerta a cada contato vira ruído que a pessoa aprende "
                + "a pular, e junto com ele ela pula o resto da tela");
        }

        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeTrue("na 13ª volta a avisar, pra o lote longo não emudecer");
    }

    // A decisão de parar depende de informação que o programa NÃO tem: só o WhatsApp Web mostra
    // restrição, e o aparelho não. Então a sequência de recusas nunca para o lote sozinha — quem
    // decide é o operador, com o `parar N` disponível pra quem quiser automatizar sem o dado.
    [Fact]
    public void Sequencia_de_recusas_NAO_para_o_lote_por_conta_propria()
    {
        var d = new BatchStopPolicy(0); // 0 = operador pediu "nunca pare"
        for (var i = 0; i < 20; i++)
        {
            d.NoAccount();
        }

        d.ShouldStop.Should().BeFalse(
            "recusa em sequência é ambígua no aparelho (lista ruim ou conta restrita), e travar no "
            + "caso comum interrompia o fluxo à toa");
    }

    [Fact]
    public void Com_teto_configurado_a_sequencia_de_recusas_para()
    {
        // O `parar N` continua valendo pra QUALQUER falha, inclusive recusa: é a forma de quem quer
        // parada automática pedir por ela.
        var d = new BatchStopPolicy(5);
        for (var i = 0; i < 5; i++)
        {
            d.NoAccount();
        }

        d.ShouldStop.Should().BeTrue();
    }

    // 🔴 A MESMA sequência de 3 recusas pesa diferente conforme o que veio antes. O contador de
    // entregas do lote INTEIRO é o que separa "a conta resolve número, são esses três" de "nada
    // resolveu ainda, nem uma vez" — e ele usa dado que o lote já tinha e ninguém lia.
    [Fact]
    public void Entrega_no_lote_nao_e_apagada_pelas_recusas_seguintes()
    {
        var d = Padrao();
        d.NadaEntregouAinda.Should().BeTrue("lote recém-começado ainda não provou nada");

        d.Delivered();
        d.NoAccount();
        d.NoAccount();
        d.NoAccount();

        d.NadaEntregouAinda.Should().BeFalse(
            "uma entrega prova que o WhatsApp resolveu um destinatário e aceitou a mensagem; recusa "
            + "depois disso não desfaz essa prova");
        d.TotalDelivered.Should().Be(1);
        d.DeveAlertarRecusas.Should().BeTrue("o alerta continua saindo, só que com peso menor");
    }

    // 🔴 TotalDelivered é fato sobre o PASSADO, e restrição pode começar no MEIO do lote. Um lote que
    // entregou 12 e depois emenda recusas não autoriza dizer "a conta resolve número": autorizava
    // antes. Sem esta regra, o aviso brando tranquilizaria justamente o chip que acabou de cair.
    [Fact]
    public void Entrega_antiga_deixa_de_absolver_quando_a_sequencia_cresce()
    {
        var d = Padrao();
        d.Delivered();
        d.NoAccount();
        d.NoAccount();
        d.NoAccount();

        d.SuspeitaRecaiSobreAConta.Should().BeFalse(
            "no limiar, com entrega recente, a causa provável ainda são esses três números");

        d.NoAccount();
        d.SuspeitaRecaiSobreAConta.Should().BeTrue(
            "passou do limiar e continuou crescendo: o que veio antes já não explica o presente");
    }

    // 🔴 A VIRADA É A NOTÍCIA. Com entregas antes, o alerta do limiar sai brando; se a sequência
    // continua, a hipótese branda acabou de ser desmentida. Sem este aviso na virada, o operador só
    // saberia 10 contatos depois — meia hora no ritmo normal.
    [Fact]
    public void Avisa_de_novo_na_virada_de_brando_para_forte()
    {
        var d = Padrao();
        d.Delivered();
        d.NoAccount();
        d.NoAccount();
        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeTrue("limiar: aviso brando");
        d.SuspeitaRecaiSobreAConta.Should().BeFalse();

        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeTrue("a hipótese branda caiu; avisar agora é a notícia");
        d.SuspeitaRecaiSobreAConta.Should().BeTrue();

        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeFalse("dito uma vez, volta a espaçar");
    }

    [Fact]
    public void Sem_entregas_o_limiar_nao_gera_dois_avisos_seguidos()
    {
        // Aqui o limiar já sai FORTE, então não há virada a comunicar e a cláusula não dispara.
        var d = Padrao();
        d.NoAccount();
        d.NoAccount();
        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeTrue();

        d.NoAccount();
        d.DeveAlertarRecusas.Should().BeFalse("repetir o mesmo alerta no contato seguinte é ruído");
    }

    // 🔴 A ÚNICA evidência de grau de CERTEZA sem root e sem WhatsApp Web: o app negou um número que o
    // espelho dele mesmo, na agenda do Android, marca como usuário. Duas fontes independentes sobre o
    // mesmo fato, discordando.
    [Fact]
    public void Duas_contradicoes_apontam_a_conta_com_certeza()
    {
        var d = Padrao();
        d.NoAccount(contradizidoPelaAgenda: true);
        d.ContaProvavelmenteRestrita.Should().BeFalse(
            "uma isolada ainda pode ser espelho velho de quem saiu do WhatsApp");

        d.NoAccount(contradizidoPelaAgenda: true);
        d.ContaProvavelmenteRestrita.Should().BeTrue();
        d.AcabouDeConfirmarContradicao.Should().BeTrue("diz com todas as letras, uma vez");

        d.NoAccount(contradizidoPelaAgenda: true);
        d.AcabouDeConfirmarContradicao.Should().BeFalse("o estado continua, o anúncio não repete");
        d.ContaProvavelmenteRestrita.Should().BeTrue();
    }

    [Fact]
    public void Numero_morto_de_verdade_no_meio_nao_apaga_as_contradicoes()
    {
        // Lista real intercala morto de verdade (espelho não sabe) com contradito. Zerar numa recusa
        // comum apagaria justamente o rastro que interessa.
        var d = Padrao();
        d.NoAccount(contradizidoPelaAgenda: true);
        d.NoAccount(contradizidoPelaAgenda: false);
        d.NoAccount(contradizidoPelaAgenda: true);

        d.ConsecutiveContradicoes.Should().Be(2);
        d.ContaProvavelmenteRestrita.Should().BeTrue();
    }

    [Fact]
    public void Entrega_zera_as_contradicoes()
    {
        // Entrega PROVA que a conta resolve número. Depois dela, contradição anterior não descreve mais
        // o presente — mesma doutrina do TotalDelivered.
        var d = Padrao();
        d.NoAccount(contradizidoPelaAgenda: true);
        d.Delivered();
        d.NoAccount(contradizidoPelaAgenda: true);

        d.ConsecutiveContradicoes.Should().Be(1);
        d.ContaProvavelmenteRestrita.Should().BeFalse();
    }

    [Fact]
    public void Sem_nenhuma_entrega_a_suspeita_recai_sobre_a_conta_ja_no_limiar()
    {
        var d = Padrao();
        for (var i = 0; i < 3; i++)
        {
            d.NoAccount();
        }

        d.SuspeitaRecaiSobreAConta.Should().BeTrue("nada resolveu ainda, nem uma vez");
    }

    [Fact]
    public void Lote_que_nunca_entregou_mantem_a_suspeita_forte()
    {
        var d = Padrao();
        for (var i = 0; i < 3; i++)
        {
            d.NoAccount();
        }

        d.NadaEntregouAinda.Should().BeTrue();
        d.TotalDelivered.Should().Be(0);
    }

    [Fact]
    public void Entrega_no_meio_absolve_a_lista()
    {
        // Uma entrega prova que o aparelho fala com o WhatsApp e que a lista não é toda lixo. Sem este
        // zeramento, um lote longo acumularia recusas espalhadas e acusaria a lista sem motivo.
        var d = Padrao();
        d.NoAccount();
        d.NoAccount();
        d.Delivered();
        d.NoAccount();

        d.ConsecutiveNoAccount.Should().Be(1);
        d.DeveAlertarRecusas.Should().BeFalse();
    }

    [Fact]
    public void Falha_de_aparelho_no_meio_nao_apaga_o_rastro_da_lista()
    {
        // O espelho do teste que protege o contador de aparelho. Se um zerasse o outro, o alerta some
        // exatamente na situação mista, que é a mais difícil de diagnosticar no olho.
        var d = Padrao();
        d.NoAccount();
        d.DeviceFailure();
        d.NoAccount();
        d.DeviceFailure();
        d.NoAccount();

        d.ConsecutiveNoAccount.Should().Be(3);
        d.DeveAlertarRecusas.Should().BeTrue();
    }
}
