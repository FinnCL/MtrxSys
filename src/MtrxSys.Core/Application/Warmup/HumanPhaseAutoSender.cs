using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Domain.Warmup;
using MtrxSys.Core.Messaging;
using MtrxSys.Core.Safety;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.Warmup;

/// <summary>Envio AUTOMÁTICO pro círculo durante a Fase Humana: o chip conversando com os SEUS
/// contatos salvos, sem você digitar tudo.
///
/// VAI PELO CAMINHO DO CHAT, NÃO PELO DISPARO — e isso é a decisão central. Aquecer pelo disparo
/// parece natural (é o fluxo do produto: grupo → importar → disparar) e funcionava antes do livro-
/// razão compartilhado existir. Hoje não funciona mais e não deve ser reaberto:
///   • o disparo chama ledger.MarkSentAsync a cada envio → o número fica "já enviado" nos 10
///     ambientes PARA SEMPRE, queimando justo as pessoas que aquecem o PRÓXIMO chip;
///   • a partir da 2ª mensagem o próprio ledger pularia o envio ("já enviado em outro ambiente"),
///     e aquecer é mandar várias ao longo de dias — daria 1 mensagem por pessoa, e só;
///   • liberar isso exigiria isentar 4 camadas (LastSentAt, consulta do ledger, MarkSent, teto),
///     cada uma um furo permanente na rede que impede reenvio acidental a cliente real.
/// Aqui nada disso é tocado: waha.SendTextAsync direto, igual à aba Chat. Por isso mandar várias
/// mensagens pra mesma pessoa é livre — não há trava a isentar.
///
/// NÃO CONSEGUE BURLAR A FASE: o gate exige INBOUND por pessoa, e robô produz saída, não resposta.
/// Se seus amigos não responderem de verdade, a fase não fecha. É o que torna seguro automatizar
/// o seu lado.</summary>
public sealed class HumanPhaseAutoSender(
    HumanPhaseGate gate,
    ISystemStateRepository systemState,
    IWarmupCircleRepository circle,
    IHumanPhaseProgressRepository progress,
    IConversationRepository conversations,
    IChatMessageRepository messages,
    IContactRepository contacts,
    BrazilPhoneValidator phones,
    IWahaClient waha,
    SpintaxExpander spintax,
    TypingSimulator typing,
    IUnitOfWork uow,
    IClock clock,
    IRandomSource rng,
    IOptions<HumanPhaseOptions> opts,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<HumanPhaseAutoSender> log)
{
    // Usado quando a config não traz frases. Vazio no options DE PROPÓSITO (o binder ANEXA a
    // arrays), mesmo padrão da curva do WarmupManager. São aberturas casuais, de conversa — não
    // material de venda: quem recebe é gente que te conhece.
    private static readonly string[] DefaultPhrases =
    [
        "{Oi|Opa|E aí}, {tudo bem|tudo certo|beleza}?",
        "{Bom dia|Oi}! {Como você está|Tudo tranquilo por aí}?",
        "{E aí|Opa}, {sumido|sumida|quanto tempo}! {Novidades|Como andam as coisas}?",
        "{Oi|Opa}, {passando pra falar oi|só passando pra dar um alô}.",
        "{Tudo bem|Tudo certo} por aí? {Qualquer coisa me chama|Se precisar, é só chamar}.",
    ];

    /// <summary>Um ciclo: manda NO MÁXIMO uma mensagem. O worker chama de tempos em tempos e o
    /// ritmo emerge dos intervalos por pessoa — sem rajada, mesmo se o ciclo rodar de minuto em
    /// minuto. Retorna true se enviou.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var snap = await gate.GetSnapshotAsync(ct);
        if (snap is null || snap.Satisfied)
        {
            return false; // fase não se aplica a este chip, ou já cumpriu: nada a aquecer
        }

        var state = await systemState.GetAsync(ct);
        if (!state.HumanPhaseAutoSendEnabled)
        {
            return false;
        }

        var o = opts.Value.AutoSend;
        // Janela impossível (ex.: 20→6, tentando dizer "à noite") nunca é verdadeira em hora nenhuma
        // e deixaria o robô MUDO em silêncio: o card diria "puxando assunto sozinho" e nada sairia,
        // sem nenhuma pista. Não dá pra adivinhar a intenção, então grita em vez de fingir.
        if (o.ActiveHourStart >= o.ActiveHourEnd)
        {
            log.LogError(
                "Fase Humana: janela ativa inválida ({Start}h→{End}h) — o início tem que ser ANTES do "
                + "fim (a janela não cruza a meia-noite). Nenhuma mensagem automática vai sair até "
                + "corrigir HumanPhase:AutoSend:ActiveHourStart/End.",
                o.ActiveHourStart, o.ActiveHourEnd);
            return false;
        }

        var now = clock.UtcNow;
        var hourBrt = now.ToOffset(TimeSpan.FromHours(-3)).Hour;
        if (hourBrt < o.ActiveHourStart || hourBrt >= o.ActiveHourEnd)
        {
            return false; // mensagem de madrugada é sinal de robô
        }

        var members = await circle.ListAsync(ct);
        if (members.Count == 0)
        {
            return false; // sem círculo não há a quem escrever (o card pede pra montar)
        }

        // Sessão fora do ar: não tenta. O envio falharia e só sujaria o log.
        var sessionId = dispatchOpts.Value.SessionId;
        if (await waha.GetSessionStatusAsync(sessionId, ct) != WahaSessionStatus.Working)
        {
            return false;
        }

        // JANELA DE LEITURA — nunca depois de hoje. TODOS os freios (intervalo, teto do dia, parar
        // de insistir) saem de RELER os próprios envios; se a janela não os alcançar, o remetente
        // perde a memória e manda a cada tick, sem limite, justo pras pessoas do seu círculo — e em
        // silêncio, porque nada "falha". Uma âncora à frente de hoje não deveria existir
        // (RestartWarmup usa a data de Brasília), mas o custo de garantir é uma linha e o custo de
        // errar é o seu círculo queimado. Verificado na prática: com a âncora um dia à frente, ele
        // reenviou a cada minuto sem nunca frear.
        var today = IClock.ToBrasiliaDate(now);
        var since = snap.StartedOn <= today ? snap.StartedOn : today;

        var candidates = await BuildCandidatesAsync(members, since, now, o, ct);
        if (candidates.Count == 0)
        {
            return false;
        }

        // Entre os elegíveis, o de saída mais antiga primeiro (quem está há mais tempo sem notícia).
        // Empate (ex.: ninguém recebeu nada ainda) resolve no sorteio, pra não seguir sempre a mesma
        // ordem da lista — ordem fixa todo dia é padrão de robô.
        var pick = candidates
            .OrderBy(c => c.LastOutboundAt ?? DateTimeOffset.MinValue)
            .Take(2)
            .ToList();
        var target = pick[rng.NextInt(0, pick.Count)];

        return await SendAsync(target, sessionId, now, ct);
    }

    private async Task<List<Candidate>> BuildCandidatesAsync(
        IReadOnlyList<WarmupCircleMember> members,
        DateOnly since,
        DateTimeOffset now,
        HumanPhaseAutoSendOptions o,
        CancellationToken ct)
    {
        // Resolve a conversa de cada pessoa ANTES de ler as mensagens — uma query de carimbos só.
        var resolved = new List<(WarmupCircleMember Member, Conversation? Conversation)>();
        foreach (var m in members)
        {
            resolved.Add((m, await ResolveConversationAsync(m.PhoneE164, ct)));
        }

        var ids = resolved.Where(r => r.Conversation is not null).Select(r => r.Conversation!.Id).ToList();
        var stamps = await progress.ListStampsForConversationsAsync(ids, since, ct);
        var byConversation = stamps.GroupBy(s => s.ConversationId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var today = IClock.ToBrasiliaDate(now);
        var candidates = new List<Candidate>();
        foreach (var (member, conversation) in resolved)
        {
            var mine = conversation is not null && byConversation.TryGetValue(conversation.Id, out var list)
                ? list
                : [];
            if (IsDue(mine, today, now, o, out var lastOutboundAt))
            {
                candidates.Add(new Candidate(member, conversation, lastOutboundAt));
            }
        }
        return candidates;
    }

    // As regras de "posso escrever pra essa pessoa agora?", todas derivadas do histórico — sem
    // estado próprio pra ficar defasado.
    private bool IsDue(
        List<MessageStamp> stamps,
        DateOnly today,
        DateTimeOffset now,
        HumanPhaseAutoSendOptions o,
        out DateTimeOffset? lastOutboundAt)
    {
        var outbound = stamps.Where(s => s.Direction == MessageDirection.Outbound).ToList();
        lastOutboundAt = outbound.Count == 0 ? null : outbound.Max(s => s.Timestamp);

        var sentToday = outbound.Count(s => IClock.ToBrasiliaDate(s.Timestamp) == today);
        if (sentToday >= o.MaxPerPersonPerDay)
        {
            return false;
        }

        // NÃO INSISTIR com quem não responde: conta as nossas desde a última resposta dela. É a
        // trava mais importante — martelar quem ficou em silêncio é o padrão que a fase evita, e um
        // humano também pararia. Quando a pessoa responder, o contador zera sozinho.
        var lastInboundAt = stamps.Where(s => s.Direction == MessageDirection.Inbound)
            .Select(s => (DateTimeOffset?)s.Timestamp)
            .DefaultIfEmpty(null)
            .Max();
        var unanswered = outbound.Count(s => lastInboundAt is null || s.Timestamp > lastInboundAt);
        if (unanswered >= o.MaxUnansweredInARow)
        {
            return false;
        }

        if (lastOutboundAt is not { } last)
        {
            return true; // nunca escrevemos: pode ir
        }
        // Intervalo ALEATÓRIO em [Min, Max], não fixo — cadência exata é assinatura de robô. O
        // sorteio é refeito a cada ciclo de propósito: como o worker chama de tempos em tempos, a
        // pessoa vira elegível no primeiro ciclo cujo sorteio caia abaixo do tempo já decorrido.
        // O efeito é uma espera aleatória dentro da faixa, sem precisar guardar o intervalo
        // sorteado em lugar nenhum. Abaixo de Min nunca passa; acima de Max sempre passa.
        var gapMinutes = rng.NextInt(o.MinGapMinutes, Math.Max(o.MinGapMinutes, o.MaxGapMinutes) + 1);
        return now - last >= TimeSpan.FromMinutes(gapMinutes);
    }

    /// <summary>Acha a conversa da pessoa do MESMO jeito que o webhook (WebhookIngestionService):
    /// primeiro pelo contato, depois pelo chatId @c.us.
    ///
    /// A ordem importa e não é estética. O webhook unifica @c.us e @lid pelo ContactId — sem isso,
    /// nós criaríamos uma conversa @c.us só de IDA enquanto a resposta chegaria por @lid numa
    /// SEGUNDA conversa, só de VOLTA. O placar veria duas conversas e NENHUMA qualificaria: o robô
    /// mandaria mensagem pra sempre e a fase nunca fecharia.</summary>
    private async Task<Conversation?> ResolveConversationAsync(string phoneE164, CancellationToken ct)
    {
        var contact = await contacts.GetByPhoneAsync(phoneE164, ct);
        if (contact is not null && await conversations.GetByContactIdAsync(contact.Id, ct) is { } byContact)
        {
            return byContact;
        }
        var chatId = WahaChatIdentifier.ExtractDigits(phoneE164) + WahaChatIdentifier.IndividualSuffix;
        return await conversations.GetByWaChatIdAsync(chatId, ct);
    }

    private async Task<bool> SendAsync(Candidate target, string sessionId, DateTimeOffset now, CancellationToken ct)
    {
        var phrases = opts.Value.AutoSend.Phrases is { Length: > 0 } configured ? configured : DefaultPhrases;
        var text = spintax.Expand(phrases[rng.NextInt(0, phrases.Length)]);

        // Manda pra conversa EXISTENTE quando há uma (preserva @lid); só cai no telefone quando é a
        // primeira mensagem — aí o WAHA canoniza o número.
        var destination = target.Conversation?.WaChatId ?? target.Member.PhoneE164;
        string waMessageId;
        try
        {
            await typing.SimulateAsync(sessionId, destination, text, ct);
            waMessageId = await waha.SendTextAsync(sessionId, destination, text, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // aquecimento é melhor-esforço: uma falha aqui não pode derrubar o worker
        catch (Exception ex)
        {
            uow.DiscardChanges();
            log.LogWarning(ex, "Fase Humana: falhou o envio automático para {Phone}; tenta no próximo ciclo.",
                target.Member.PhoneE164);
            return false;
        }
#pragma warning restore CA1031

        // Daqui pra baixo a mensagem JÁ SAIU (irreversível). A gravação vai num try PRÓPRIO e o
        // retorno é true de qualquer forma — mesma separação que o DispatchEngine faz. Se a
        // gravação estourasse dentro do try do envio, o ciclo seguinte não veria a mensagem (os
        // freios só enxergam o que está gravado) e MANDARIA DE NOVO: duas mensagens em um minuto
        // pra mesma pessoa, exatamente o padrão de robô que a fase existe pra evitar.
        await TryRecordOutboundAsync(target, text, waMessageId, now, ct);
        log.LogInformation(
            "Fase Humana: mensagem automática para {Phone} ({Name}).",
            target.Member.PhoneE164, target.Member.Name ?? "sem nome");
        return true;
    }

    private async Task TryRecordOutboundAsync(
        Candidate target, string text, string waMessageId, DateTimeOffset now, CancellationToken ct)
    {
        try
        {
            await RecordOutboundAsync(target, text, waMessageId, now, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // a mensagem já saiu; registrar é melhor-esforço
        catch (Exception ex)
        {
            uow.DiscardChanges();
            // Grave o suficiente pra ser Warning: sem o registro, o próximo ciclo re-manda pra essa
            // pessoa. O eco do webhook ainda pode cobrir (o de-dupe é pelo mesmo "core" de id).
            log.LogWarning(ex,
                "Fase Humana: enviei para {Phone} mas NÃO registrei no chat; o ciclo seguinte pode "
                + "repetir a mensagem se o eco do webhook não cobrir.", target.Member.PhoneE164);
        }
#pragma warning restore CA1031
    }

    // Grava a saída no chat. NÃO é enfeite: o eco do webhook é instável (foi por isso que o disparo
    // passou a gravar explicitamente), e sem o registro o próprio remetente não enxergaria a
    // mensagem que acabou de mandar — mandaria de novo no ciclo seguinte, em rajada. O de-dupe pelo
    // "core" do id evita duplicar quando o eco chegar.
    private async Task RecordOutboundAsync(
        Candidate target, string text, string waMessageId, DateTimeOffset now, CancellationToken ct)
    {
        var coreId = WahaChatIdentifier.ExtractMessageCore(waMessageId);
        if (string.IsNullOrEmpty(coreId))
        {
            coreId = $"warmup_{Guid.NewGuid():N}";
        }
        if (await messages.GetByWaMessageIdAsync(coreId, ct) is not null)
        {
            return; // o eco venceu a corrida
        }

        var conversation = target.Conversation;
        if (conversation is null)
        {
            // A conversa PRECISA nascer vinculada a um contato, e por isso criamos o contato se ele
            // ainda não existir. É o elo de que o webhook depende pra unificar @c.us e @lid
            // (WebhookIngestionService: acha por waChatId, senão por ContactId).
            //
            // Sem o vínculo o furo é REAL e silencioso, justo no caso comum — chip novo, círculo
            // recém-montado da agenda, ninguém ainda é contato: mandamos pro chatId CONSTRUÍDO
            // (digits@c.us), que nem sempre é o real (a pessoa pode aparecer por @lid, e o WhatsApp
            // resolve o 9º dígito BR pra outra forma — ver CheckNumberExistsAsync). Quando ela
            // responde, o chatId real não casa, o ContactId é null e não há por onde ligar → nasce
            // uma 2ª conversa. Aí o placar vê uma conversa só de IDA e outra só de VOLTA, NENHUMA
            // qualifica, e o disparo fica travado pra sempre mesmo com a pessoa tendo respondido.
            //
            // Criar o contato aqui NÃO contradiz "o círculo não é Contact": o webhook já cria um
            // contato pra qualquer mensagem individual (inclusive a resposta dela, daqui a pouco) —
            // só estamos antecipando o que ia acontecer de qualquer jeito. E ele nasce sem
            // ImportedByPhone, então o gate por chip do disparo o PULA: não vira alvo de campanha.
            var contact = await contacts.GetByPhoneAsync(target.Member.PhoneE164, ct);
            if (contact is null)
            {
                contact = Contact.Create(
                    id: Guid.NewGuid(),
                    phone: phones.NormalizeTrusted(target.Member.PhoneE164),
                    name: target.Member.Name,
                    groupTag: null,
                    theme: null,
                    optInAt: now);
                await contacts.AddAsync(contact, ct);
            }
            conversation = Conversation.Create(
                id: Guid.NewGuid(),
                waChatId: WahaChatIdentifier.ExtractDigits(target.Member.PhoneE164) + WahaChatIdentifier.IndividualSuffix,
                contactId: contact.Id,
                title: target.Member.Name ?? target.Member.PhoneE164,
                isGroup: false,
                createdAt: now);
            await conversations.AddAsync(conversation, ct);
        }

        await messages.AddAsync(
            ChatMessage.Create(
                id: Guid.NewGuid(),
                conversationId: conversation.Id,
                waMessageId: coreId,
                direction: MessageDirection.Outbound,
                authorPhone: null,
                body: text,
                timestamp: now),
            ct);
        conversation.TouchLastMessage(now, text);
        await conversations.UpdateAsync(conversation, ct);
        await uow.SaveChangesAsync(ct);
    }

    private sealed record Candidate(
        WarmupCircleMember Member, Conversation? Conversation, DateTimeOffset? LastOutboundAt);
}
