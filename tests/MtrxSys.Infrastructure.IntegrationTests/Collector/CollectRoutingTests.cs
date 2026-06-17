using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Groups;
using MtrxSys.Core.Domain.Groups;
using NSubstitute;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// Roteamento da fonte no CollectGroupLinksUseCase (tudo mockado, sem rede/banco): nicho + Google
/// configurado → usa a busca (e NÃO o Telegram); sem nicho, ou Google desligado → cai no Telegram.
/// É o coração da feature de busca por nicho.
/// </summary>
public sealed class CollectRoutingTests
{
    private readonly IGroupLinkSource _telegram = Substitute.For<IGroupLinkSource>();
    private readonly IGroupLinkSearchSource _search = Substitute.For<IGroupLinkSearchSource>();
    private readonly IGroupLinkRepository _links = Substitute.For<IGroupLinkRepository>();
    private readonly IWhatsAppInviteValidator _validator = Substitute.For<IWhatsAppInviteValidator>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IRandomSource _rng = Substitute.For<IRandomSource>();

    private CollectGroupLinksUseCase Build(CollectorOptions opts)
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        _links.GetByCodesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, GroupLink>());
        _links.ListForEnrichmentAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<GroupLink>());
        _links.ListForEnrichmentByKeywordAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<GroupLink>());
        // validator devolve null por padrão (transitório) → não interfere no roteamento testado aqui.
        return new CollectGroupLinksUseCase(
            _telegram, _search, _validator, _links, _uow, _clock, _rng, Options.Create(opts));
    }

    [Fact]
    public async Task Com_nicho_e_google_configurado_usa_a_busca_e_nao_o_telegram()
    {
        _search.IsConfigured.Returns(true);
        _search.SearchAsync("bet", 30, Arg.Any<CancellationToken>())
            .Returns(new[] { new RawGroupLink("ABCDEFGHIJKL", "https://chat.whatsapp.com/ABCDEFGHIJKL", "google") });
        var useCase = Build(new CollectorOptions { TelegramChannels = ["chan1"], MaxResultsPerSearch = 30 });

        var result = await useCase.ExecuteAsync("bet", CancellationToken.None);

        await _search.Received(1).SearchAsync("bet", 30, Arg.Any<CancellationToken>());
        await _telegram.DidNotReceive().FetchChannelAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        result.NewLinks.Should().Be(1);
    }

    [Fact]
    public async Task Sem_nicho_usa_o_telegram_e_nao_a_busca()
    {
        _search.IsConfigured.Returns(true); // configurado, mas sem keyword não deve ser usado
        _telegram.FetchChannelAsync("chan1", Arg.Any<CancellationToken>())
            .Returns(new ChannelHarvest([], []));
        var useCase = Build(new CollectorOptions { TelegramChannels = ["chan1"] });

        await useCase.ExecuteAsync(null, CancellationToken.None);

        await _telegram.Received().FetchChannelAsync("chan1", Arg.Any<CancellationToken>());
        await _search.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Com_nicho_mas_google_desligado_cai_no_telegram()
    {
        _search.IsConfigured.Returns(false);
        _telegram.FetchChannelAsync("chan1", Arg.Any<CancellationToken>())
            .Returns(new ChannelHarvest([], []));
        var useCase = Build(new CollectorOptions { TelegramChannels = ["chan1"] });

        await useCase.ExecuteAsync("bet", CancellationToken.None);

        await _telegram.Received().FetchChannelAsync("chan1", Arg.Any<CancellationToken>());
        await _search.DidNotReceive().SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>());
    }
}
