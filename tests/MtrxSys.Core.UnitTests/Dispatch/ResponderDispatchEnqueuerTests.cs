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
using MtrxSys.Core.Safety;
using NSubstitute;
using Xunit;

namespace MtrxSys.Core.UnitTests.Dispatch;

/// <summary>Trava o enfileiramento por-respondedor na hora (o "oi" manual → resposta → "Na fila +
/// Respondeu", sem esperar o reset da meia-noite). Foca no que importa: só age na fase, é idempotente,
/// pausa a fila, e não faz nada sem template.</summary>
public sealed class ResponderDispatchEnqueuerTests
{
    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly IDispatchJobRepository _jobs = Substitute.For<IDispatchJobRepository>();
    private readonly IMessageTemplateRepository _templates = Substitute.For<IMessageTemplateRepository>();
    private readonly ISystemStateRepository _systemState = Substitute.For<ISystemStateRepository>();
    private readonly ISharedPhoneLedger _ledger = Substitute.For<ISharedPhoneLedger>();
    private readonly IDailySendCountsRepository _counts = Substitute.For<IDailySendCountsRepository>();
    private readonly IRandomSource _rng = Substitute.For<IRandomSource>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private readonly SystemStateAggregate _state = SystemStateAggregate.CreateInitial();
    private readonly Contact _contact = MakeEngagedContact();
    private static readonly Guid ContactId = Guid.NewGuid();

    public ResponderDispatchEnqueuerTests()
    {
        // Meio-dia UTC → data de Brasília coincide (sem ambiguidade de fronteira). Marco 18/07, hoje 20/07.
        _clock.UtcNow.Returns(new DateTimeOffset(new DateOnly(2026, 7, 20).ToDateTime(new TimeOnly(12, 0)), TimeSpan.Zero));
        _state.RestartWarmup(new DateOnly(2026, 7, 18));
        _systemState.GetAsync(Arg.Any<CancellationToken>()).Returns(_state);

        // Ledger em no-op (Off): FilterOutSuppressedAsync (extensão) só devolve a lista.
        _ledger.IsEnforcing.Returns(false);
        // Fase ATIVA por padrão: 1 dia ativo < 3 dias de trava.
        _counts.CountActiveDaysBeforeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(1);
        // 1 template ativo no pote.
        _templates.ListAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<MessageTemplate> { MessageTemplate.Create(Guid.NewGuid(), MessageSlot.Greeting, "Oi!") });
        // Fila vazia (não está enviando) e o contato passa o dedup (não tem job na fila).
        _jobs.GetEarliestPendingScheduledAtAsync(Arg.Any<CancellationToken>()).Returns((DateTimeOffset?)null);
        _contacts.ListByFilterAsync(Arg.Any<ContactFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<Contact> { _contact });
        _rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(0);
    }

    private static Contact MakeEngagedContact()
    {
        var c = Contact.Create(Guid.NewGuid(), PhoneNumber.FromValidatedE164("+5571999990001"),
            name: "Fulano", groupTag: null, theme: null, optInAt: DateTimeOffset.UtcNow);
        c.ChangeStage(ContactStage.Qualified, DateTimeOffset.UtcNow); // respondeu → engajado
        return c;
    }

    private ResponderDispatchEnqueuer Build() => new(
        _contacts, _jobs, _templates, _systemState, _ledger,
        new WarmingPhaseService(_counts,
            new WarmupManager(_counts, _systemState, _clock, Options.Create(new WarmupOptions())),
            _clock, Options.Create(new DispatchOptions { WarmingResponderOnlyDays = 3 })),
        _rng, _clock, _uow, NullLogger<ResponderDispatchEnqueuer>.Instance);

    [Fact]
    public async Task Respondedor_na_fase_vira_job_na_fila_e_pausa()
    {
        var created = await Build().EnqueueSingleResponderAsync(ContactId, CancellationToken.None);

        created.Should().BeTrue();
        await _jobs.Received(1).AddAsync(Arg.Any<DispatchJob>(), Arg.Any<CancellationToken>());
        await _jobs.Received(1).DeleteHistoryByContactAsync(ContactId, Arg.Any<CancellationToken>());
        _state.IsManuallyPaused.Should().BeTrue("nada pode sair sem o operador clicar Iniciar envios");
    }

    [Fact]
    public async Task Fora_da_fase_nao_enfileira()
    {
        _counts.CountActiveDaysBeforeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>()).Returns(3); // cumpriu os 3 dias

        var created = await Build().EnqueueSingleResponderAsync(ContactId, CancellationToken.None);

        created.Should().BeFalse();
        await _jobs.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Sem_template_ativo_nao_enfileira()
    {
        _templates.ListAllAsync(Arg.Any<CancellationToken>()).Returns(new List<MessageTemplate>());

        var created = await Build().EnqueueSingleResponderAsync(ContactId, CancellationToken.None);

        created.Should().BeFalse();
        await _jobs.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Fact]
    public async Task Ja_dispatchado_ou_na_fila_nao_duplica_nem_muta()
    {
        // Dedup (ExcludeAlreadyDispatched: já recebeu OU já tem job na fila) removeu o contato →
        // filtro volta vazio. Não pode nem criar job nem MEXER no histórico (idempotência sem efeito).
        _contacts.ListByFilterAsync(Arg.Any<ContactFilter>(), Arg.Any<CancellationToken>())
            .Returns(new List<Contact>());

        var created = await Build().EnqueueSingleResponderAsync(ContactId, CancellationToken.None);

        created.Should().BeFalse();
        await _jobs.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _jobs.DidNotReceiveWithAnyArgs().DeleteHistoryByContactAsync(default, default);
    }

    [Fact]
    public async Task Ja_enviando_enfileira_sem_pausar()
    {
        // Fila JÁ rodando (tem pendente e não está manual-pausada) → o operador já autorizou; não
        // pausa (não sabota o envio em curso), só acrescenta o job.
        _jobs.GetEarliestPendingScheduledAtAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTimeOffset(2026, 7, 20, 10, 0, 0, TimeSpan.Zero));

        var created = await Build().EnqueueSingleResponderAsync(ContactId, CancellationToken.None);

        created.Should().BeTrue();
        await _jobs.Received(1).AddAsync(Arg.Any<DispatchJob>(), Arg.Any<CancellationToken>());
        _state.IsManuallyPaused.Should().BeFalse("não pausa uma fila que já está enviando");
        await _systemState.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }

    [Fact]
    public async Task Fila_ja_pausada_enfileira_sem_reescrever_o_estado()
    {
        // Caso comum do aquecimento em regime: a fila já está MANUAL-pausada. Cria o job, mas NÃO
        // reescreve system_state — é o que evita disputar o token xmin do singleton quando várias
        // respostas chegam juntas (senão a perdedora reverteria o job junto).
        _state.Pause(SystemStateAggregate.ManualPauseReason);

        var created = await Build().EnqueueSingleResponderAsync(ContactId, CancellationToken.None);

        created.Should().BeTrue();
        await _jobs.Received(1).AddAsync(Arg.Any<DispatchJob>(), Arg.Any<CancellationToken>());
        await _systemState.DidNotReceiveWithAnyArgs().UpdateAsync(default!, default);
    }
}
