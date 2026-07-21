using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Domain.Warmup;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Contacts;

/// <summary>
/// Regra do auto-círculo: contatos introduzidos nos N primeiros dias de CALENDÁRIO do aquecimento do
/// chip entram no Círculo de Aquecimento; do dia N+1 em diante, não. Aqui N = WarmingResponderOnlyDays.
/// </summary>
public sealed class WarmupSeedEnrollerTests
{
    private readonly ISystemStateRepository _state = Substitute.For<ISystemStateRepository>();
    private readonly IWarmupCircleRepository _circle = Substitute.For<IWarmupCircleRepository>();
    private readonly IClock _clock = Substitute.For<IClock>();

    // Aquecimento começou em 15/07 → janela dos 3 primeiros dias = {15, 16, 17}; dia 18 já é fora.
    private static readonly DateOnly StartedOn = new(2026, 7, 15);

    private WarmupSeedEnroller Build(DateOnly? startedOn, DateTimeOffset now, int days = 3)
    {
        var state = SystemStateAggregate.CreateInitial();
        if (startedOn is { } s)
        {
            state.RestartWarmup(s);
        }
        _state.GetAsync(Arg.Any<CancellationToken>()).Returns(state);
        _clock.UtcNow.Returns(now);
        _circle.ListAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<WarmupCircleMember>)new List<WarmupCircleMember>());
        return new WarmupSeedEnroller(
            _state, _circle, Options.Create(new DispatchOptions { WarmingResponderOnlyDays = days }), _clock);
    }

    private static (string PhoneE164, string? Name)[] Batch(params string[] phones) =>
        phones.Select(p => (p, (string?)null)).ToArray();

    // 12:00 UTC = 09:00 em Brasília (UTC-3) → mesma data; evita cruzar a meia-noite na conversão.
    private static DateTimeOffset Noon(int day) => new(2026, 7, day, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Enrolls_new_contacts_within_first_days()
    {
        var sut = Build(StartedOn, Noon(17)); // dia 3 da janela (ainda dentro)

        await sut.EnrollIfSeedPhaseAsync(Batch("+5511999990001", "+5511999990002"), CancellationToken.None);

        await _circle.Received(1).AddAsync(
            Arg.Is<WarmupCircleMember>(m => m.PhoneE164 == "+5511999990001"), Arg.Any<CancellationToken>());
        await _circle.Received(1).AddAsync(
            Arg.Is<WarmupCircleMember>(m => m.PhoneE164 == "+5511999990002"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_enroll_from_day_four_onward()
    {
        var sut = Build(StartedOn, Noon(18)); // 1º dia FORA da janela (dia 4)

        await sut.EnrollIfSeedPhaseAsync(Batch("+5511999990001"), CancellationToken.None);

        await _circle.DidNotReceive().AddAsync(Arg.Any<WarmupCircleMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Does_not_enroll_when_warmup_not_started()
    {
        var sut = Build(startedOn: null, Noon(15)); // sem marco → não auto-inscreve

        await sut.EnrollIfSeedPhaseAsync(Batch("+5511999990001"), CancellationToken.None);

        await _circle.DidNotReceive().AddAsync(Arg.Any<WarmupCircleMember>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Is_idempotent_skips_phones_already_in_circle()
    {
        var sut = Build(StartedOn, Noon(16));
        _circle.ListAsync(Arg.Any<CancellationToken>()).Returns((IReadOnlyList<WarmupCircleMember>)
        [
            WarmupCircleMember.Create(Guid.NewGuid(), "+5511999990001", "já no círculo", DateTimeOffset.UtcNow),
        ]);

        await sut.EnrollIfSeedPhaseAsync(Batch("+5511999990001", "+5511999990002"), CancellationToken.None);

        await _circle.DidNotReceive().AddAsync(
            Arg.Is<WarmupCircleMember>(m => m.PhoneE164 == "+5511999990001"), Arg.Any<CancellationToken>());
        await _circle.Received(1).AddAsync(
            Arg.Is<WarmupCircleMember>(m => m.PhoneE164 == "+5511999990002"), Arg.Any<CancellationToken>());
    }
}
