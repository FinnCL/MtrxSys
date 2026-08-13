using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Reporting;

namespace MtrxSys.Core.UnitTests.Reporting;

public sealed class RelatorioDeEnviosTests
{
    private static LinhaDeEnvio Linha(
        ResultadoDoEnvio resultado,
        FalhaCausa causa = FalhaCausa.Nenhuma,
        string? entrega = null,
        string dia = "2026-08-13",
        bool contradito = false) =>
        new(
            Quando: DateTimeOffset.Parse($"{dia}T10:00:00-03:00", System.Globalization.CultureInfo.InvariantCulture),
            Numero: "5584999990000",
            Nome: "Fulano",
            Variante: 1,
            Resultado: resultado,
            Entrega: entrega,
            Erro: causa is FalhaCausa.Nenhuma ? null : "algum erro",
            Texto: "oi",
            Contradito: contradito,
            Abertura: "numero",
            Causa: causa);

    [Theory]
    [InlineData("sim", ResultadoDoEnvio.Enviado)]
    [InlineData("incerto", ResultadoDoEnvio.Incerto)]
    [InlineData("nao", ResultadoDoEnvio.NaoSaiu)]
    public void Interpretar_le_a_coluna_enviado(string bruto, ResultadoDoEnvio esperado) =>
        RelatorioDeEnvios.Interpretar(bruto).Should().Be(esperado);

    [Theory]
    [InlineData("")]
    [InlineData("talvez")]
    [InlineData(null)]
    public void Interpretar_devolve_nulo_no_desconhecido(string? bruto) =>
        RelatorioDeEnvios.Interpretar(bruto).Should().BeNull();

    [Theory]
    [InlineData("NumeroSemConta", FalhaCausa.NumeroSemConta)]
    [InlineData("Timeout", FalhaCausa.Timeout)]
    public void LerCausa_reconhece_o_nome_gravado(string bruto, FalhaCausa esperado) =>
        RelatorioDeEnvios.LerCausa(bruto).Should().Be(esperado);

    /// <summary>🔴 Log anterior à coluna de causa precisa continuar abrindo. Vazio, lixo e número solto
    /// viram "Nenhuma" em vez de estourar, senão um CSV velho derruba o relatório inteiro.</summary>
    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("CausaQueNaoExiste")]
    [InlineData("999")]
    [InlineData("numerosemconta")]
    public void LerCausa_tolera_o_que_nao_reconhece(string? bruto) =>
        RelatorioDeEnvios.LerCausa(bruto).Should().Be(FalhaCausa.Nenhuma);

    [Fact]
    public void Resumir_conta_cada_desfecho_no_balde_certo()
    {
        var r = RelatorioDeEnvios.Resumir(
        [
            Linha(ResultadoDoEnvio.Enviado, entrega: "delivered"),
            Linha(ResultadoDoEnvio.Enviado, entrega: "read"),
            Linha(ResultadoDoEnvio.Enviado, entrega: "sent"),
            Linha(ResultadoDoEnvio.Incerto, FalhaCausa.ToqueNaoConfirmado),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.TelaBloqueada),
        ]);

        r.Tentativas.Should().Be(7);
        r.Enviadas.Should().Be(3);
        r.EntregasConfirmadas.Should().Be(2); // "sent" NÃO é entrega
        r.Incertas.Should().Be(1);
        r.SemConta.Should().Be(2);
        r.OutrasFalhas.Should().Be(1);
        r.Contradicoes.Should().Be(0);
    }

    /// <summary>🔴 A invariante que o laço do lote e a releitura do CSV também respeitam: o incerto
    /// gasta cota. Se as três contagens discordarem, o teto do dia seguinte sai errado.</summary>
    [Fact]
    public void Cota_gasta_soma_enviado_e_incerto_e_ignora_falha()
    {
        var r = RelatorioDeEnvios.Resumir(
        [
            Linha(ResultadoDoEnvio.Enviado),
            Linha(ResultadoDoEnvio.Incerto, FalhaCausa.ToqueNaoConfirmado),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.Timeout),
        ]);

        r.CotaGasta.Should().Be(2);
    }

    /// <summary>O incerto entra em PorCausa: ele é falha no fecho do lote, e as duas contagens precisam
    /// bater com o que o console imprime.</summary>
    [Fact]
    public void PorCausa_agrupa_da_maior_para_a_menor_e_fecha_em_cem_por_cento()
    {
        var r = RelatorioDeEnvios.Resumir(
        [
            Linha(ResultadoDoEnvio.Enviado),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta),
            Linha(ResultadoDoEnvio.Incerto, FalhaCausa.ToqueNaoConfirmado),
        ]);

        r.PorCausa.Should().HaveCount(2);
        r.PorCausa[0].Causa.Should().Be(FalhaCausa.NumeroSemConta);
        r.PorCausa[0].Quantidade.Should().Be(3);
        r.PorCausa[0].Fracao.Should().BeApproximately(0.75, 0.001);
        r.PorCausa[1].Causa.Should().Be(FalhaCausa.ToqueNaoConfirmado);
        r.PorCausa.Sum(c => c.Fracao).Should().BeApproximately(1.0, 0.001);
    }

    [Fact]
    public void PorCausa_fica_vazio_e_nao_divide_por_zero_quando_tudo_saiu()
    {
        var r = RelatorioDeEnvios.Resumir([Linha(ResultadoDoEnvio.Enviado, entrega: "read")]);

        r.PorCausa.Should().BeEmpty();
        r.OutrasFalhas.Should().Be(0);
    }

    /// <summary>🔴 O incerto conta como SAÍDA no dia. A linha do dia mede volume, e volume é conversa
    /// aberta: pô-lo entre as recusas faria a curva de aquecimento ver um dia mais leve que o real.</summary>
    [Fact]
    public void PorDia_sai_em_ordem_cronologica_e_conta_o_incerto_como_saida()
    {
        var r = RelatorioDeEnvios.Resumir(
        [
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta, dia: "2026-08-12"),
            Linha(ResultadoDoEnvio.Enviado, entrega: "read", dia: "2026-08-11"),
            Linha(ResultadoDoEnvio.Incerto, FalhaCausa.ToqueNaoConfirmado, dia: "2026-08-12"),
        ]);

        r.PorDia.Select(d => d.Dia).Should().Equal("2026-08-11", "2026-08-12");
        r.PorDia[0].Enviadas.Should().Be(1);
        r.PorDia[0].EntregasConfirmadas.Should().Be(1);
        r.PorDia[1].Enviadas.Should().Be(1);   // o incerto
        r.PorDia[1].NaoSairam.Should().Be(1);
    }

    /// <summary>Contradição é o sinal precoce de restrição: o app nega o número e a agenda do próprio
    /// aparelho discorda dele.</summary>
    [Fact]
    public void Contradicoes_sao_contadas_a_parte()
    {
        var r = RelatorioDeEnvios.Resumir(
        [
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta, contradito: true),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta, contradito: true),
            Linha(ResultadoDoEnvio.NaoSaiu, FalhaCausa.NumeroSemConta),
        ]);

        r.Contradicoes.Should().Be(2);
        r.SemConta.Should().Be(3);
    }

    /// <summary>O dia sai no offset em que foi gravado, e não no fuso de quem abre a planilha: um
    /// relatório aberto noutro fuso não pode mover envios de dia e mexer na curva de aquecimento.</summary>
    [Fact]
    public void Dia_respeita_o_offset_gravado()
    {
        var madrugada = new LinhaDeEnvio(
            new DateTimeOffset(2026, 8, 13, 1, 0, 0, TimeSpan.FromHours(-3)),
            "5584999990000", null, 1, ResultadoDoEnvio.Enviado, "read", null, "oi", false, "numero",
            FalhaCausa.Nenhuma);

        madrugada.Dia.Should().Be("2026-08-13");
    }

    [Fact]
    public void Resumir_aceita_lista_vazia()
    {
        var r = RelatorioDeEnvios.Resumir([]);

        r.Tentativas.Should().Be(0);
        r.CotaGasta.Should().Be(0);
        r.PorDia.Should().BeEmpty();
        r.PorCausa.Should().BeEmpty();
    }
}
