using FluentAssertions;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.UnitTests.Safety;

/// <summary>A sugestão de volume derivada do histórico do próprio aparelho.</summary>
/// <remarks>
/// 🔴 Mora no Core, e não dentro do console, pelo mesmo motivo do <see cref="BatchStopPolicy"/> e do
/// <c>WhatsAppAccountState.Parse</c>: é decisão com casos de borda, e o projeto do CLI não tem testes.
/// <para>O que estes testes protegem, mais que os números: que a sugestão SAI DO DADO e não de uma
/// tabela. Se alguém um dia trocar isto por uma curva fixa, é aqui que a intenção estará escrita.</para>
/// </remarks>
public sealed class ChipHistoryTests
{
    [Fact]
    public void Aparelho_sem_historico_comeca_pequeno()
    {
        // Não existe número seguro: o WarmupManager registra chips perdidos com DUAS mensagens e com
        // ZERO. Existe número pequeno o bastante pro estrago ser pequeno se der errado.
        var s = ChipHistory.Sugerir(diasAtivos: 0, ultimoDia: null);

        s.Sugestao.Should().Be(ChipHistory.SugestaoChipNovo);
        s.Motivo.Should().Contain("10 dias", "o período de risco máximo precisa aparecer na tela");
    }

    // 🔴 A FRASE tem que bater com o que o painel mostra na linha de cima. "Sem histórico" para um
    // aparelho que ja disparou hoje seria contradicao na mesma tela.
    [Fact]
    public void Chip_que_disparou_hoje_mas_sem_dia_fechado_nao_diz_sem_historico()
    {
        var s = ChipHistory.Sugerir(diasAtivos: 1, ultimoDia: null);

        s.Sugestao.Should().Be(ChipHistory.SugestaoChipNovo, "sem dia fechado, não há base pra crescer");
        s.Motivo.Should().Contain("FECHADO");
        s.Motivo.Should().NotContain("sem histórico");
    }

    [Fact]
    public void Dia_registrado_mas_sem_nenhum_envio_conta_como_sem_historico()
    {
        // Lote inteiro segurado pela checagem de agenda, ou interrompido no primeiro contato: o dia
        // existe no CSV e não ensina nada sobre o chip.
        var s = ChipHistory.Sugerir(diasAtivos: 3, ultimoDia: new DiaDoChip(0, 12, 0));

        s.Sugestao.Should().Be(ChipHistory.SugestaoChipNovo);
        s.DiasAtivos.Should().Be(3);
    }

    [Fact]
    public void Dia_limpo_cresce_devagar_sobre_o_que_o_chip_ja_fez()
    {
        // 🔴 A SUGESTÃO SAI DO DADO. 30 é o que ESTE chip fez ontem, não um número de tabela. Dobrar
        // chegaria ao platô numa semana, e pressa é o que queima chip.
        var s = ChipHistory.Sugerir(diasAtivos: 6, ultimoDia: new DiaDoChip(30, 1, 22));

        s.Sugestao.Should().BeGreaterThan(30);
        s.Sugestao.Should().BeLessThan(60, "crescer não é dobrar");
        s.Motivo.Should().Contain("limpo");
    }

    [Fact]
    public void Dia_com_muita_recusa_encolhe_pela_metade()
    {
        // Encolher e não parar: recusa alta pode ser lista ruim, que não diz nada sobre o chip.
        // Responder ao sinal sem tratar suspeita como certeza.
        var s = ChipHistory.Sugerir(diasAtivos: 4, ultimoDia: new DiaDoChip(20, 15, 10));

        s.Sugestao.Should().Be(10);
        s.Motivo.Should().Contain("recusa");
    }

    [Fact]
    public void A_sugestao_nunca_passa_do_plato_do_projeto()
    {
        // Sem teto, semanas boas seguidas empurrariam a sugestão pra números que ninguém defende.
        // 120 é o único número emprestado de fora, e ele é LIMITE, não meta.
        var s = ChipHistory.Sugerir(diasAtivos: 30, ultimoDia: new DiaDoChip(115, 0, 90));

        s.Sugestao.Should().Be(ChipHistory.TetoSugestao);
    }

    // 🔴 As duas fronteiras vêm de origens INDEPENDENTES: os 10 dias das fontes de 2026 (risco máximo
    // no início) e os 20 da curva feita à mão do WarmupManager, que chega ao platô por ali. Duas
    // origens apontando pra mesma ordem de grandeza é o que dá alguma confiança nas duas.
    [Theory]
    [InlineData(0, FaseDoChip.Novo)]
    [InlineData(10, FaseDoChip.Novo)]
    [InlineData(11, FaseDoChip.Aquecendo)]
    [InlineData(19, FaseDoChip.Aquecendo)]
    [InlineData(20, FaseDoChip.Maduro)]
    [InlineData(60, FaseDoChip.Maduro)]
    public void Fase_do_chip_pelos_dias_de_disparo(int dias, FaseDoChip esperada) =>
        ChipHistory.FaseDe(dias).Should().Be(esperada);

    // 🔴 O TESTE QUE VALIDA A FÓRMULA INTEIRA. O console vinha configurado em 150-360s desde antes
    // desta conta existir. Se dividir a janela pelo volume reproduz esse ajuste no platô, então a
    // fórmula está descrevendo a mesma realidade que alguém enxergou à mão — e o ajuste antigo era o
    // de um chip MADURO, usado num chip novo.
    [Fact]
    public void A_formula_reproduz_o_ajuste_historico_do_console_no_plato()
    {
        var (min, max) = ChipHistory.IntervaloPara(mensagensDoDia: 120, janelaHoras: 8);

        min.Should().BeCloseTo(144, 10);
        max.Should().BeCloseTo(336, 10);
    }

    [Fact]
    public void Volume_baixo_espalha_muito_mais()
    {
        // Cinco mensagens a 150-360s despacham o dia em 25 minutos e depois silenciam. Concentração
        // seguida de silêncio parece mais máquina do que as mesmas cinco espalhadas pelo dia.
        var (min, max) = ChipHistory.IntervaloPara(mensagensDoDia: 5, janelaHoras: 8);

        min.Should().BeGreaterThan(3000, "com 5 no dia, o espaçamento é de mais de uma hora");
        max.Should().BeGreaterThan(min);
    }

    [Fact]
    public void Nunca_desce_do_piso_de_seguranca()
    {
        // As fontes apontam 1 msg/min como o teto onde a velocidade pesa. O piso de 150s é 2,5x mais
        // folgado, e nenhuma conta pode furá-lo por mais mensagens que o operador queira mandar.
        var (min, _) = ChipHistory.IntervaloPara(mensagensDoDia: 5000, janelaHoras: 8);

        min.Should().Be(ChipHistory.IntervaloMinimoSegundos);
    }

    [Fact]
    public void Janela_aberta_nao_vira_24h_na_conta()
    {
        // Espalhar por 24h calcularia o intervalo sobre um horário em que mandar mensagem já é, por si
        // só, comportamento de robô.
        var (minAberta, _) = ChipHistory.IntervaloPara(mensagensDoDia: 12, janelaHoras: 24);
        var (min12, _) = ChipHistory.IntervaloPara(mensagensDoDia: 12, janelaHoras: 12);

        minAberta.Should().Be(min12);
    }

    [Fact]
    public void Nunca_sugere_zero_depois_de_um_dia_que_entregou()
    {
        // Um dia com 1 envio e nenhuma recusa não pode virar "não mande nada hoje": isso travaria o
        // aquecimento justamente no chip que está começando certo.
        var s = ChipHistory.Sugerir(diasAtivos: 2, ultimoDia: new DiaDoChip(1, 0, 1));

        s.Sugestao.Should().BeGreaterThan(0);
    }
}
