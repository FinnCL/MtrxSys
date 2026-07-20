using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Dispatch;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Messages;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Domain.Warmup;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;
using NSubstitute;
using Xunit;

namespace MtrxSys.Core.UnitTests.Dispatch;

/// <summary>Trava o lote diário da fase HÍBRIDA: abre com círculo, espalha o resto entre os frios,
/// pausa a fila; e não reconstrói se já estiver enviando.</summary>
public sealed class HybridCycleEnqueuerTests
{
    private readonly IWarmupCircleRepository _circle = Substitute.For<IWarmupCircleRepository>();
    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly IDispatchJobRepository _jobs = Substitute.For<IDispatchJobRepository>();
    private readonly IMessageTemplateRepository _templates = Substitute.For<IMessageTemplateRepository>();
    private readonly ISystemStateRepository _systemState = Substitute.For<ISystemStateRepository>();
    private readonly IDailySendCountsRepository _counts = Substitute.For<IDailySendCountsRepository>();
    private readonly ISharedPhoneLedger _ledger = Substitute.For<ISharedPhoneLedger>();
    private readonly IRandomSource _rng = Substitute.For<IRandomSource>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly SystemStateAggregate _state = SystemStateAggregate.CreateInitial();
    private readonly List<DispatchJob> _enqueued = [];

    public HybridCycleEnqueuerTests()
    {
        _clock.UtcNow.Returns(new DateTimeOffset(2026, 7, 25, 12, 0, 0, TimeSpan.Zero));
        _state.ReconcileWarmupPhone("+5511970000000", new DateOnly(2026, 7, 25)); // chip conectado
        _systemState.GetAsync(Arg.Any<CancellationToken>()).Returns(_state);
        _ledger.IsEnforcing.Returns(false); // FilterOutSuppressed vira passthrough
        _templates.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<MessageTemplate> { MessageTemplate.Create(Guid.NewGuid(), MessageSlot.Greeting, "Oi!") });
        _counts.CountActiveDaysBeforeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(0);
        _jobs.GetEarliestPendingScheduledAtAsync(Arg.Any<CancellationToken>()).Returns((DateTimeOffset?)null);
        _rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(0);
        _jobs.When(j => j.AddAsync(Arg.Any<DispatchJob>(), Arg.Any<CancellationToken>()))
            .Do(ci => _enqueued.Add(ci.Arg<DispatchJob>()));
    }

    // Teto folgado (curva [100], 0 dias ativos → limite 100) pra o cap não constranger o teste.
    private WarmupManager Warmup() =>
        new(_counts, _systemState, _clock, Options.Create(new WarmupOptions { Curve = [100] }));

    private HybridCycleEnqueuer Build() => new(
        _circle, _contacts, _jobs, _templates, _systemState, Warmup(), _ledger,
        new BrazilPhoneValidator(), _rng, _clock, _uow, NullLogger<HybridCycleEnqueuer>.Instance);

    private static Contact MakeContact(string e164, bool engaged)
    {
        var c = Contact.Create(Guid.NewGuid(), PhoneNumber.FromValidatedE164(e164), "x", null, null, DateTimeOffset.UtcNow);
        if (engaged) c.ChangeStage(ContactStage.Qualified, DateTimeOffset.UtcNow);
        return c;
    }

    [Fact]
    public async Task Monta_lote_abrindo_com_circulo_espalhado_e_pausa()
    {
        var c1 = MakeContact("+5511999990001", engaged: true);
        var c2 = MakeContact("+5511999990002", engaged: true);
        _circle.ListAsync(Arg.Any<CancellationToken>()).Returns(new List<WarmupCircleMember>
        {
            WarmupCircleMember.Create(Guid.NewGuid(), c1.Phone.E164, "c1", DateTimeOffset.UtcNow),
            WarmupCircleMember.Create(Guid.NewGuid(), c2.Phone.E164, "c2", DateTimeOffset.UtcNow),
        });
        _contacts.GetByPhonesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, Contact> { [c1.Phone.E164] = c1, [c2.Phone.E164] = c2 });
        var cold = Enumerable.Range(0, 6).Select(i => MakeContact($"+551198888{i:D4}", engaged: false)).ToList();
        _contacts.ListByFilterAsync(Arg.Any<ContactFilter>(), Arg.Any<CancellationToken>()).Returns(cold);

        var n = await Build().BuildAndEnqueueAsync(CancellationToken.None);

        n.Should().Be(8); // 2 do círculo + 6 frios
        var circleIds = new[] { c1.Id, c2.Id };
        var order = _enqueued.OrderBy(j => j.ScheduledAt).Select(j => j.ContactId).ToList();
        circleIds.Should().Contain(order[0]); // ABRE com círculo
        var circlePositions = order
            .Select((id, idx) => (id, idx))
            .Where(x => circleIds.Contains(x.id))
            .Select(x => x.idx)
            .ToList();
        circlePositions.Should().Equal(0, 4); // 2 do círculo ESPALHADOS (não adjacentes)
        _state.IsManuallyPaused.Should().BeTrue("nada sai sem Iniciar envios");
        // Anti-regressão do bug crítico: o círculo tem que passar o gate-por-chip do motor, então
        // precisa ficar com ImportedByPhone = chip conectado (senão viraria "Pulada").
        c1.ImportedByPhone.Should().Be("+5511970000000");
        c2.ImportedByPhone.Should().Be("+5511970000000");
    }

    [Fact]
    public async Task Ja_enviando_nao_reconstroi()
    {
        // Tem pendente + não pausado = enviando → não mexe na fila.
        _jobs.GetEarliestPendingScheduledAtAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTimeOffset(2026, 7, 25, 10, 0, 0, TimeSpan.Zero));

        var n = await Build().BuildAndEnqueueAsync(CancellationToken.None);

        n.Should().Be(0);
        await _jobs.DidNotReceiveWithAnyArgs().ClearPendingAsync(default);
        await _jobs.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }
}
