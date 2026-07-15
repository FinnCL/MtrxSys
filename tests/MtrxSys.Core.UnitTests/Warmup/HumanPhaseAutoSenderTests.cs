using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.Warmup;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Domain.Warmup;
using MtrxSys.Core.Messaging;
using MtrxSys.Core.Safety;
using NSubstitute;

namespace MtrxSys.Core.UnitTests.Warmup;

/// <summary>
/// Regras do envio automático da Fase Humana. O que se prova aqui é sobretudo o que ele NÃO faz:
/// a diferença entre "aquecimento" e "robô de spam" é toda feita de freios.
/// </summary>
public sealed class HumanPhaseAutoSenderTests
{
    private static readonly DateOnly Cut = new(2026, 7, 14);
    private static readonly DateOnly Anchor = new(2026, 7, 15);
    private const string Phone = "+5571999998888";
    private const string ChatId = "5571999998888@c.us";
    private static readonly Guid ConversationId = Guid.NewGuid();
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly ISystemStateRepository _state = Substitute.For<ISystemStateRepository>();
    private readonly IHumanPhaseProgressRepository _progress = Substitute.For<IHumanPhaseProgressRepository>();
    private readonly IWarmupCircleRepository _circle = Substitute.For<IWarmupCircleRepository>();
    private readonly IConversationRepository _conversations = Substitute.For<IConversationRepository>();
    private readonly IChatMessageRepository _messages = Substitute.For<IChatMessageRepository>();
    private readonly IContactRepository _contacts = Substitute.For<IContactRepository>();
    private readonly IWahaClient _waha = Substitute.For<IWahaClient>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly IRandomSource _rng = Substitute.For<IRandomSource>();

    private readonly SystemStateAggregate _stateAggregate = SystemStateAggregate.CreateInitial();

    public HumanPhaseAutoSenderTests()
    {
        // Cenário-base: chip novo (ancorado após o corte), robô LIGADO, 1 pessoa no círculo com
        // conversa já existente, meio-dia de Brasília, sessão no ar, nada conversado ainda.
        _stateAggregate.RestartWarmup(Anchor);
        _stateAggregate.SetHumanPhaseAutoSendEnabled(true);
        _state.GetAsync(Ct).Returns(_stateAggregate);

        SetNow(Anchor, hourBrt: 12);
        _waha.GetSessionStatusAsync(Arg.Any<string>(), Ct).Returns(WahaSessionStatus.Working);
        _waha.SendTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Ct).Returns("wa-msg-1");

        _circle.ListAsync(Ct).Returns([
            WarmupCircleMember.Create(Guid.NewGuid(), Phone, "João", DateTimeOffset.UtcNow),
        ]);
        _conversations.GetByWaChatIdAsync(ChatId, Ct).Returns(
            Conversation.Create(ConversationId, ChatId, null, "João", false, DateTimeOffset.UtcNow));
        _contacts.GetByPhoneAsync(Phone, Ct).Returns((Contact?)null);
        _progress.ListConversationTalliesAsync(Arg.Any<DateOnly>(), Ct).Returns([]);
        SetStamps();

        // rng determinístico: sempre o menor valor (gap mínimo, 1ª frase, 1º candidato).
        _rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(call => call.ArgAt<int>(0));
        _rng.NextDouble().Returns(0.5);
    }

    private void SetNow(DateOnly day, int hourBrt) =>
        _clock.UtcNow.Returns(new DateTimeOffset(day.ToDateTime(new TimeOnly(hourBrt, 0)), TimeSpan.FromHours(-3)));

    // O fake FILTRA por `since`, igual ao repositório real (que faz timestamp >= início do dia-
    // Brasília de `since`). Sem isso os carimbos voltariam sempre, o filtro nunca seria exercitado,
    // e o teste da janela viraria placebo — foi exatamente assim que o bug da âncora futura passou
    // batido nos testes e só apareceu rodando de verdade.
    private void SetStamps(params (MessageDirection Direction, DateTimeOffset At)[] stamps) =>
        _progress.ListStampsForConversationsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Ct)
            .Returns(call =>
            {
                var since = call.ArgAt<DateOnly>(1);
                var from = new DateTimeOffset(since.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(-3));
                return stamps
                    .Where(s => s.At >= from)
                    .Select(s => new MessageStamp(ConversationId, s.Direction, s.At))
                    .ToList();
            });

    private static DateTimeOffset At(DateOnly day, int hourBrt) =>
        new(day.ToDateTime(new TimeOnly(hourBrt, 0)), TimeSpan.FromHours(-3));

    private HumanPhaseAutoSender Build(HumanPhaseAutoSendOptions? auto = null)
    {
        var opts = Options.Create(new HumanPhaseOptions
        {
            EffectiveFrom = Cut,
            MinDays = 3,
            MinPeople = 5,
            MinInbound = 3,
            MinOutbound = 3,
            AutoSend = auto ?? new HumanPhaseAutoSendOptions(),
        });
        var gate = new HumanPhaseGate(_state, _progress, opts);
        var dispatchOpts = Options.Create(new DispatchOptions { SessionId = "default" });
        return new HumanPhaseAutoSender(
            gate, _state, _circle, _progress, _conversations, _messages, _contacts,
            new MtrxSys.Core.Validation.BrazilPhoneValidator(), _waha,
            new SpintaxExpander(_rng), new TypingSimulator(_waha, _rng, dispatchOpts), _uow, _clock,
            _rng, opts, dispatchOpts, NullLogger<HumanPhaseAutoSender>.Instance);
    }

    private async Task<bool> Run(HumanPhaseAutoSendOptions? auto = null) =>
        await Build(auto).RunOnceAsync(Ct);

    [Fact]
    public async Task Envia_quando_tudo_esta_no_lugar()
    {
        (await Run()).Should().BeTrue();

        await _waha.Received(1).SendTextAsync("default", ChatId, Arg.Any<string>(), Ct);
    }

    [Fact]
    public async Task Manda_no_maximo_UMA_por_ciclo()
    {
        // O ritmo vem dos intervalos, não do tick. Mesmo com 3 pessoas prontas, sai uma só —
        // é o que impede o worker de virar rajada por mais rápido que ele bata.
        _circle.ListAsync(Ct).Returns([
            WarmupCircleMember.Create(Guid.NewGuid(), "+5571900000001", "A", DateTimeOffset.UtcNow),
            WarmupCircleMember.Create(Guid.NewGuid(), "+5571900000002", "B", DateTimeOffset.UtcNow),
            WarmupCircleMember.Create(Guid.NewGuid(), "+5571900000003", "C", DateTimeOffset.UtcNow),
        ]);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Ct).Returns((Conversation?)null);

        (await Run()).Should().BeTrue();

        await _waha.Received(1).SendTextAsync("default", Arg.Any<string>(), Arg.Any<string>(), Ct);
    }

    // ── Os freios ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Nao_envia_quando_a_fase_nao_se_aplica()
    {
        // Chip de produção (ancorado antes do corte): o robô nem existe pra ele.
        _stateAggregate.RestartWarmup(Cut.AddDays(-1));
        _stateAggregate.SetHumanPhaseAutoSendEnabled(true);

        (await Run()).Should().BeFalse();
        await _waha.DidNotReceive().SendTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Ct);
    }

    [Fact]
    public async Task Nao_envia_quando_a_fase_ja_fechou()
    {
        // Cumprida a fase, o robô se cala SOZINHO — sem depender de alguém lembrar de desligar.
        _progress.CountOutboundActiveDaysAsync(Arg.Any<DateOnly>(), Ct).Returns(3);
        _progress.ListConversationTalliesAsync(Arg.Any<DateOnly>(), Ct).Returns(
            Enumerable.Range(0, 5)
                .Select(i => new ConversationTally(Guid.NewGuid(), $"p{i}", $"55719000000{i}@c.us", 3, 3))
                .ToList());

        (await Run()).Should().BeFalse();
        await _waha.DidNotReceive().SendTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Ct);
    }

    [Fact]
    public async Task Nao_envia_com_o_toggle_desligado()
    {
        _stateAggregate.SetHumanPhaseAutoSendEnabled(false);

        (await Run()).Should().BeFalse();
    }

    [Fact]
    public async Task Nao_envia_de_madrugada()
    {
        // 3h da manhã: mandar mensagem nessa hora é sinal de robô.
        SetNow(Anchor, hourBrt: 3);

        (await Run()).Should().BeFalse();
    }

    [Fact]
    public async Task Nao_envia_com_a_sessao_fora_do_ar()
    {
        _waha.GetSessionStatusAsync(Arg.Any<string>(), Ct).Returns(WahaSessionStatus.ScanQrCode);

        (await Run()).Should().BeFalse();
        await _waha.DidNotReceive().SendTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Ct);
    }

    [Fact]
    public async Task Nao_envia_com_o_circulo_vazio()
    {
        _circle.ListAsync(Ct).Returns([]);

        (await Run()).Should().BeFalse();
    }

    [Fact]
    public async Task Respeita_o_intervalo_minimo_entre_mensagens_pra_mesma_pessoa()
    {
        // Escrevemos há 10 min; o mínimo é 25. Cadência apertada é assinatura de robô.
        SetStamps((MessageDirection.Outbound, _clock.UtcNow.AddMinutes(-10)));

        (await Run(new HumanPhaseAutoSendOptions { MinGapMinutes = 25, MaxGapMinutes = 25 }))
            .Should().BeFalse();
    }

    [Fact]
    public async Task Volta_a_enviar_depois_do_intervalo()
    {
        SetStamps(
            (MessageDirection.Outbound, _clock.UtcNow.AddMinutes(-40)),
            (MessageDirection.Inbound, _clock.UtcNow.AddMinutes(-35)));

        (await Run(new HumanPhaseAutoSendOptions { MinGapMinutes = 25, MaxGapMinutes = 25 }))
            .Should().BeTrue();
    }

    [Fact]
    public async Task Para_de_insistir_com_quem_nao_responde()
    {
        // ESTE É O FREIO MAIS IMPORTANTE: 2 mensagens nossas seguidas sem nenhuma resposta → cala.
        // Martelar quem ficou em silêncio é exatamente o padrão que a fase existe pra evitar (e um
        // humano também pararia). O intervalo já passou — o que segura aqui é o silêncio dela.
        SetStamps(
            (MessageDirection.Outbound, At(Anchor, 8)),
            (MessageDirection.Outbound, At(Anchor, 9)));

        (await Run(new HumanPhaseAutoSendOptions { MaxUnansweredInARow = 2 })).Should().BeFalse();
    }

    [Fact]
    public async Task Volta_a_falar_quando_a_pessoa_responde()
    {
        // A resposta dela ZERA o contador de não-respondidas: a conversa retomou.
        SetStamps(
            (MessageDirection.Outbound, At(Anchor, 8)),
            (MessageDirection.Outbound, At(Anchor, 9)),
            (MessageDirection.Inbound, At(Anchor, 10)));

        (await Run(new HumanPhaseAutoSendOptions { MaxUnansweredInARow = 2 })).Should().BeTrue();
    }

    [Fact]
    public async Task Respeita_o_teto_por_pessoa_por_dia()
    {
        // 4 trocas hoje já bastam; a 5ª não sai, mesmo com a pessoa respondendo tudo.
        SetStamps(
            (MessageDirection.Outbound, At(Anchor, 8)), (MessageDirection.Inbound, At(Anchor, 8)),
            (MessageDirection.Outbound, At(Anchor, 9)), (MessageDirection.Inbound, At(Anchor, 9)),
            (MessageDirection.Outbound, At(Anchor, 10)), (MessageDirection.Inbound, At(Anchor, 10)),
            (MessageDirection.Outbound, At(Anchor, 11)), (MessageDirection.Inbound, At(Anchor, 11)));

        (await Run(new HumanPhaseAutoSendOptions { MaxPerPersonPerDay = 4 })).Should().BeFalse();
    }

    [Fact]
    public async Task O_teto_diario_zera_no_dia_seguinte()
    {
        // As 4 de ontem não contam pro teto de hoje — o teto é POR DIA de Brasília.
        SetStamps(
            (MessageDirection.Outbound, At(Anchor, 8)), (MessageDirection.Inbound, At(Anchor, 9)),
            (MessageDirection.Outbound, At(Anchor, 10)), (MessageDirection.Inbound, At(Anchor, 11)),
            (MessageDirection.Outbound, At(Anchor, 12)), (MessageDirection.Inbound, At(Anchor, 13)),
            (MessageDirection.Outbound, At(Anchor, 14)), (MessageDirection.Inbound, At(Anchor, 15)));
        SetNow(Anchor.AddDays(1), hourBrt: 12);

        (await Run(new HumanPhaseAutoSendOptions { MaxPerPersonPerDay = 4 })).Should().BeTrue();
    }

    [Fact]
    public async Task Le_os_proprios_envios_mesmo_com_a_ancora_a_frente_de_hoje()
    {
        // REGRESSÃO — pego rodando de verdade, não no papel. Todos os freios saem de RELER o que já
        // mandamos. Com a âncora um dia à frente de hoje, a janela de leitura excluía as próprias
        // mensagens: o remetente perdia a memória e disparava a CADA tick, sem intervalo, sem teto e
        // sem parar de insistir — em silêncio, porque nada "falha". A janela agora é limitada a
        // hoje, então os carimbos são sempre alcançados e os freios valem.
        _stateAggregate.RestartWarmup(Anchor.AddDays(1)); // âncora amanhã
        _stateAggregate.SetHumanPhaseAutoSendEnabled(true);
        SetStamps(
            (MessageDirection.Outbound, At(Anchor, 10)),
            (MessageDirection.Outbound, At(Anchor, 11)));

        (await Run(new HumanPhaseAutoSendOptions { MaxUnansweredInARow = 2 })).Should().BeFalse();
        await _waha.DidNotReceive().SendTextAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Ct);
    }

    [Fact]
    public async Task A_janela_de_leitura_nunca_passa_de_hoje()
    {
        _stateAggregate.RestartWarmup(Anchor.AddDays(5));
        _stateAggregate.SetHumanPhaseAutoSendEnabled(true);
        var today = IClock.ToBrasiliaDate(_clock.UtcNow);

        await Run();

        await _progress.Received().ListStampsForConversationsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Is<DateOnly>(d => d <= today),
            Ct);
    }

    // ── O que separa este remetente do disparo ───────────────────────────────────────────────────

    [Fact]
    public async Task Usa_a_conversa_EXISTENTE_em_vez_do_telefone()
    {
        // Se mandasse pro telefone, criaria uma conversa @c.us paralela enquanto a resposta chegaria
        // por @lid noutra — o placar veria duas conversas, uma só de ida e outra só de volta, e
        // NENHUMA qualificaria: o robô falaria pra sempre e a fase nunca fecharia.
        var lidChat = "157239574847645@lid";
        var contact = Contact.Create(
            Guid.NewGuid(), PhoneNumber.FromValidatedE164(Phone), "João", null, null, null);
        _contacts.GetByPhoneAsync(Phone, Ct).Returns(contact);
        _conversations.GetByContactIdAsync(contact.Id, Ct).Returns(
            Conversation.Create(ConversationId, lidChat, contact.Id, "João", false, DateTimeOffset.UtcNow));

        await Run();

        await _waha.Received(1).SendTextAsync("default", lidChat, Arg.Any<string>(), Ct);
    }

    [Fact]
    public async Task Vincula_um_contato_a_conversa_que_cria()
    {
        // REGRESSÃO. Caso COMUM: chip novo, círculo montado da agenda, ninguém é contato ainda.
        // A conversa que criamos usa um chatId CONSTRUÍDO (digits@c.us) que nem sempre é o real —
        // a pessoa pode aparecer por @lid, e o WhatsApp resolve o 9º dígito BR pra outra forma.
        // Se ela nascesse com ContactId null, a resposta não casaria por waChatId, o webhook não
        // teria por onde ligar (ele acha por waChatId OU por ContactId) e abriria uma 2ª conversa:
        // uma só de ida, outra só de volta, NENHUMA qualifica, e o disparo ficaria travado pra
        // sempre mesmo com a pessoa tendo respondido.
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Ct).Returns((Conversation?)null);
        _contacts.GetByPhoneAsync(Phone, Ct).Returns((Contact?)null);

        await Run();

        await _contacts.Received(1).AddAsync(Arg.Is<Contact>(c => c.Phone.E164 == Phone), Ct);
        await _conversations.Received(1).AddAsync(Arg.Is<Conversation>(c => c.ContactId != null), Ct);
    }

    [Fact]
    public async Task Reusa_o_contato_existente_em_vez_de_duplicar()
    {
        var contact = Contact.Create(
            Guid.NewGuid(), PhoneNumber.FromValidatedE164(Phone), "João", null, null, null);
        _conversations.GetByWaChatIdAsync(Arg.Any<string>(), Ct).Returns((Conversation?)null);
        _contacts.GetByPhoneAsync(Phone, Ct).Returns(contact);
        _conversations.GetByContactIdAsync(contact.Id, Ct).Returns((Conversation?)null);

        await Run();

        await _contacts.DidNotReceive().AddAsync(Arg.Any<Contact>(), Ct);
        await _conversations.Received(1).AddAsync(Arg.Is<Conversation>(c => c.ContactId == contact.Id), Ct);
    }

    [Fact]
    public async Task Falha_ao_gravar_nao_vira_reenvio()
    {
        // REGRESSÃO. A mensagem JÁ SAIU (irreversível). Se a gravação estoura e tratamos como
        // "falhou", o ciclo seguinte não vê a mensagem (os freios só enxergam o que está gravado) e
        // manda DE NOVO — duas em um minuto pra mesma pessoa. O envio tem que contar como feito.
        _messages.AddAsync(Arg.Any<ChatMessage>(), Ct).Returns(_ => throw new InvalidOperationException("banco caiu"));

        var sent = await Run();

        sent.Should().BeTrue("a mensagem saiu; tratar como falha faria o próximo ciclo repetir");
        await _waha.Received(1).SendTextAsync("default", Arg.Any<string>(), Arg.Any<string>(), Ct);
    }

    // Sem teste pra janela inválida (Start >= End) DE PROPÓSITO: com ela o robô já não enviava
    // antes da guarda (nenhuma hora satisfaz a condição), então um teste de "não envia" passaria
    // sem a guarda — placebo. O que a guarda acrescenta é o LOG que explica o silêncio ao operador,
    // e asfixiar um ILogger num assert custa mais do que vale.

    [Fact]
    public async Task Registra_a_saida_no_chat()
    {
        // Sem gravar, o próprio remetente não veria a mensagem que acabou de mandar e repetiria no
        // ciclo seguinte, em rajada — o eco do webhook é instável demais pra confiar.
        await Run();

        await _messages.Received(1).AddAsync(
            Arg.Is<ChatMessage>(m => m.Direction == MessageDirection.Outbound && m.ConversationId == ConversationId),
            Ct);
    }
}
