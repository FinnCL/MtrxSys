using FluentAssertions;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.UnitTests.SystemState;

/// <summary>
/// Marco de leitura da caixa de entrada do EMULADOR — o que substitui o webhook do WAHA no modo
/// Emulador. Errar aqui não dá erro: dá silêncio. E silêncio no "ouvir" significa opt-out ignorado,
/// ou seja, continuar disparando pra quem pediu pra sair — que é exatamente o que queima chip.
/// </summary>
public sealed class InboundMarkerTests
{
    private static SystemStateAggregate New() => SystemStateAggregate.CreateInitial();

    [Fact]
    public void Comeca_do_zero_para_ler_a_caixa_inteira_na_primeira_vez()
    {
        New().InboundLastRowId.Should().Be(0);
    }

    [Fact]
    public void Avanca_para_frente()
    {
        var s = New();

        s.AdvanceInboundMarker(42);

        s.InboundLastRowId.Should().Be(42);
    }

    [Fact]
    public void Nao_retrocede()
    {
        // Retroceder faria o poller varrer a caixa inteira a cada ciclo, gastando o adb que o disparo
        // usa pra enviar. A ingestão deduplica, então não duplicaria no Chat — mas o custo é real.
        var s = New();
        s.AdvanceInboundMarker(100);

        s.AdvanceInboundMarker(7);

        s.InboundLastRowId.Should().Be(100);
    }

    [Fact]
    public void Troca_de_chip_ZERA_o_marco()
    {
        // O caso que mata o "ouvir" em silêncio: chip novo = banco de mensagens novo no aparelho, com os
        // `_id` recomeçando do 1. Se o marco continuasse em 5000, o poller pediria "o que veio depois do
        // 5000" num banco cujo maior id é 3 — e nunca mais acharia nada. Sem erro, sem log.
        var s = New();
        s.ReconcileWarmupPhone("+557193919318", new DateOnly(2026, 7, 25));
        s.AdvanceInboundMarker(5000);

        var trocou = s.ReconcileWarmupPhone("+557199999999", new DateOnly(2026, 7, 26));

        trocou.Should().BeTrue();
        s.InboundLastRowId.Should().Be(0, "o banco do aparelho novo recomeça a numeração");
    }

    [Fact]
    public void Mesmo_chip_PRESERVA_o_marco()
    {
        // O reconcile roda a cada ciclo do dispatcher. Se ele zerasse o marco sem troca real, toda
        // mensagem já lida voltaria pra fila de ingestão a cada ciclo.
        var s = New();
        s.ReconcileWarmupPhone("+557193919318", new DateOnly(2026, 7, 25));
        s.AdvanceInboundMarker(5000);

        var trocou = s.ReconcileWarmupPhone("+557193919318", new DateOnly(2026, 7, 26));

        trocou.Should().BeFalse();
        s.InboundLastRowId.Should().Be(5000);
    }

    [Fact]
    public void Leitura_vazia_do_numero_conectado_nao_zera_nada()
    {
        // adb mudo / WAHA oscilando devolve vazio. Tratar isso como "trocou de chip" apagaria o marco e
        // reprocessaria tudo — por isso o reconcile ignora leitura vazia de propósito.
        var s = New();
        s.ReconcileWarmupPhone("+557193919318", new DateOnly(2026, 7, 25));
        s.AdvanceInboundMarker(5000);

        s.ReconcileWarmupPhone("", new DateOnly(2026, 7, 26));

        s.InboundLastRowId.Should().Be(5000);
    }
}
