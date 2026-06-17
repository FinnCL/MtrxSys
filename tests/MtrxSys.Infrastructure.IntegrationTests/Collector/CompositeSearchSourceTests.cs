using FluentAssertions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Infrastructure.Collector;
using NSubstitute;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// Roteamento do CompositeSearchSource (fontes mockadas): primário com resultado não chama a reserva;
/// primário vazio cai na reserva; sem primário usa a reserva direto; e quando o primário FALHA e a
/// reserva salva, o status avisa. É o que garante "a busca não morre quando o Serper esgota".
/// </summary>
public sealed class CompositeSearchSourceTests
{
    private static RawGroupLink Link(string code) => new(code, $"https://chat.whatsapp.com/{code}", "test");

    private static IGroupLinkSearchSource Source(bool configured, string engine, IReadOnlyList<RawGroupLink>? results = null)
    {
        var s = Substitute.For<IGroupLinkSearchSource>();
        s.IsConfigured.Returns(configured);
        s.Engine.Returns(engine);
        s.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(results ?? []);
        return s;
    }

    [Fact]
    public async Task Primario_com_resultado_nao_chama_a_reserva()
    {
        var primary = Source(true, "Serper", [Link("AAAAAAAAAAAA")]);
        var secondary = Source(true, "SearXNG");
        var composite = new CompositeSearchSource(primary, secondary, new InMemorySearchStatus());

        var r = await composite.SearchAsync("bet", 30, CancellationToken.None);

        r.Should().HaveCount(1);
        await secondary.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Primario_vazio_cai_na_reserva()
    {
        var primary = Source(true, "Serper", []);
        var secondary = Source(true, "SearXNG", [Link("BBBBBBBBBBBB")]);
        var composite = new CompositeSearchSource(primary, secondary, new InMemorySearchStatus());

        var r = await composite.SearchAsync("bet", 30, CancellationToken.None);

        r.Should().HaveCount(1);
        await secondary.Received(1).SearchAsync("bet", 30, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Sem_primario_usa_a_reserva_direto()
    {
        var primary = Source(false, "Serper");
        var secondary = Source(true, "SearXNG", [Link("CCCCCCCCCCCC")]);
        var composite = new CompositeSearchSource(primary, secondary, new InMemorySearchStatus());

        var r = await composite.SearchAsync("bet", 30, CancellationToken.None);

        r.Should().HaveCount(1);
        await primary.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void IsConfigured_e_Engine_refletem_o_primario_quando_ha_chave()
    {
        var withSerper = new CompositeSearchSource(Source(true, "Serper"), Source(true, "SearXNG"), new InMemorySearchStatus());
        withSerper.IsConfigured.Should().BeTrue();
        withSerper.Engine.Should().Be("Serper");

        var onlySearx = new CompositeSearchSource(Source(false, "Serper"), Source(true, "SearXNG"), new InMemorySearchStatus());
        onlySearx.Engine.Should().Be("SearXNG");

        new CompositeSearchSource(Source(false, "Serper"), Source(false, "SearXNG"), new InMemorySearchStatus())
            .IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Quando_o_primario_falha_e_a_reserva_salva_o_status_avisa()
    {
        var status = new InMemorySearchStatus();
        var primary = Source(true, "Serper", []);
        // Simula o Serper reportando erro de cota (como faz de verdade) antes de devolver vazio.
        primary.When(x => x.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()))
            .Do(_ => status.SetLastError("Serper recusou (HTTP 429): limite de requisições atingido."));
        var secondary = Source(true, "SearXNG", [Link("DDDDDDDDDDDD")]);
        var composite = new CompositeSearchSource(primary, secondary, status);

        await composite.SearchAsync("bet", 30, CancellationToken.None);

        status.LastError.Should().Contain("429");
        status.LastError.Should().Contain("SearXNG", "o painel precisa avisar que está usando a reserva");
    }
}
