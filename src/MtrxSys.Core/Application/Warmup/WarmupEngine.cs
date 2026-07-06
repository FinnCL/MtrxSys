using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Application.Warmup;

/// <summary>Motor CENTRAL de aquecimento de conversa. Um ciclo = (1) garante que os membros entraram
/// nos grupos legítimos configurados (passivo), (2) se estiver na hora ativa e passou o gap, escolhe um
/// PAR do pool e roda uma conversa curta de MÃO DUPLA (A→B, B→A...) com typing + delays naturais,
/// respeitando a rampa de teto/dia por membro. Best-effort ponta a ponta: nenhum erro derruba o ciclo.
/// É o cenário mais SEGURO que existe (conversa entre conhecidos), então não ameaça as contas.</summary>
public sealed class WarmupEngine(
    IOptions<WarmupEngineOptions> opts,
    IPoolWahaClientFactory poolFactory,
    IWarmupContentProvider content,
    IRandomSource rng,
    IClock clock,
    ISystemStateRepository systemState,
    WarmupState state,
    ILogger<WarmupEngine> log)
{
    public async Task RunCycleAsync(CancellationToken ct)
    {
        var o = opts.Value;
        if (!o.Enabled)
        {
            return; // kill-switch de deploy: feature não configurada neste ambiente
        }
        if (o.Members.Count < 2)
        {
            return; // mão dupla precisa de >= 2 membros; a UI/endpoint avisa a config incompleta
        }

        // Toggle da UI (persistido). Leitura fresca a cada ciclo — o worker é singleton, mas o
        // GetAsync abre um escopo/DbContext novo por ciclo, então enxerga o start/stop do operador.
        var st = await systemState.GetAsync(ct);
        if (!st.WarmupEngineEnabled)
        {
            return;
        }

        var today = IClock.ToBrasiliaDate(clock.UtcNow);
        state.EnsureStarted(today);

        // Grupos: entrar 1x por membro/grupo (passivo). Independe da hora/gap — é setup.
        await EnsureGroupsJoinedAsync(o, ct);

        // Só conversa dentro das horas ativas de Brasília.
        var hourBrt = clock.UtcNow.ToOffset(TimeSpan.FromHours(-3)).Hour;
        if (hourBrt < o.ActiveHourStart || hourBrt >= o.ActiveHourEnd)
        {
            return;
        }

        // Gap entre conversas (espalha ao longo do dia).
        if (clock.UtcNow < state.NextConversationAt)
        {
            return;
        }

        var (a, b) = PickPair(o.Members);

        var limit = TodayLimit(o, state.StartedOn ?? today, today);
        if (state.SentToday(a.Name, today) >= limit || state.SentToday(b.Name, today) >= limit)
        {
            // Par saturado hoje: tenta outro par em breve (gap curto), sem estourar o teto.
            state.NextConversationAt = clock.UtcNow + TimeSpan.FromMinutes(rng.NextInt(2, 6));
            return;
        }

        var convo = content.BuildConversation();
        await RunConversationAsync(o, a, b, convo, today, ct);

        var gap = rng.NextInt(o.MinGapMinutes, Math.Max(o.MinGapMinutes + 1, o.MaxGapMinutes + 1));
        state.NextConversationAt = clock.UtcNow + TimeSpan.FromMinutes(gap);
    }

    private async Task RunConversationAsync(
        WarmupEngineOptions o, WarmupMemberOptions a, WarmupMemberOptions b,
        IReadOnlyList<WarmupTurn> convo, DateOnly today, CancellationToken ct)
    {
        for (var i = 0; i < convo.Count; i++)
        {
            var turn = convo[i];
            var sender = turn.SenderSlot == 0 ? a : b;
            var recipient = turn.SenderSlot == 0 ? b : a;
            try
            {
                var client = poolFactory.CreateFor(sender);
                var typingMs = rng.NextInt(o.TypingMinSeconds, Math.Max(o.TypingMinSeconds + 1, o.TypingMaxSeconds + 1)) * 1000;
                await client.StartTypingAsync(sender.SessionId, recipient.PhoneE164, ct);
                await Task.Delay(typingMs, ct);
                try
                {
                    await client.StopTypingAsync(sender.SessionId, recipient.PhoneE164, CancellationToken.None);
                }
#pragma warning disable CA1031
                catch
                {
                    // best-effort cleanup do typing
                }
#pragma warning restore CA1031
                await client.SendTextAsync(sender.SessionId, recipient.PhoneE164, turn.Text, ct);
                state.RecordSent(sender.Name, today);
                log.LogInformation("Aquecimento: {From} → {To}", sender.Name, recipient.Name);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                log.LogWarning(ex, "Aquecimento: turno {From}→{To} falhou; sigo a conversa.", sender.Name, recipient.Name);
            }
#pragma warning restore CA1031

            if (i < convo.Count - 1)
            {
                var replyDelay = rng.NextInt(o.ReplyDelayMinSeconds, Math.Max(o.ReplyDelayMinSeconds + 1, o.ReplyDelayMaxSeconds + 1));
                await Task.Delay(TimeSpan.FromSeconds(replyDelay), ct);
            }
        }
    }

    private async Task EnsureGroupsJoinedAsync(WarmupEngineOptions o, CancellationToken ct)
    {
        if (o.GroupInviteLinks.Count == 0)
        {
            return;
        }
        foreach (var member in o.Members)
        {
            if (!member.JoinGroups)
            {
                continue;
            }
            foreach (var link in o.GroupInviteLinks)
            {
                if (string.IsNullOrWhiteSpace(link) || state.AlreadyJoined(member.Name, link))
                {
                    continue;
                }
                try
                {
                    var client = poolFactory.CreateFor(member);
                    await client.JoinGroupByInviteAsync(member.SessionId, link, ct);
                    state.MarkJoined(member.Name, link);
                    log.LogInformation("Aquecimento: {Member} entrou no grupo.", member.Name);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
#pragma warning disable CA1031
                catch (Exception ex)
                {
                    // Marca como tentado pra NÃO martelar um link morto todo ciclo.
                    state.MarkJoined(member.Name, link);
                    log.LogWarning(ex, "Aquecimento: {Member} não entrou num grupo (link inválido/expirado?); não re-tento.", member.Name);
                }
#pragma warning restore CA1031
            }
        }
    }

    private (WarmupMemberOptions A, WarmupMemberOptions B) PickPair(List<WarmupMemberOptions> members)
    {
        var i = rng.NextInt(0, members.Count);
        int j;
        do
        {
            j = rng.NextInt(0, members.Count);
        }
        while (j == i);
        return (members[i], members[j]);
    }

    // Rampa linear: dia 0 = Start; sobe até Max ao longo de RampDays; depois platô no Max.
    private static int TodayLimit(WarmupEngineOptions o, DateOnly startedOn, DateOnly today)
    {
        var dayIndex = Math.Max(0, today.DayNumber - startedOn.DayNumber);
        if (o.RampDays <= 0 || dayIndex >= o.RampDays)
        {
            return o.MessagesPerDayMax;
        }
        var span = o.MessagesPerDayMax - o.MessagesPerDayStart;
        return o.MessagesPerDayStart + (int)Math.Round((double)span * dayIndex / o.RampDays);
    }
}
