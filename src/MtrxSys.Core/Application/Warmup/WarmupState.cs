namespace MtrxSys.Core.Application.Warmup;

/// <summary>Estado em memória do motor de aquecimento (singleton). Guarda a contagem diária por membro
/// (pra rampa), a agenda da próxima conversa (o gap) e os grupos já entrados (pra não reentrar). Reset
/// por dia (Brasília). Volátil de propósito: um restart zera o dia (fica mais conservador — envia
/// menos, o lado seguro) e reinicia a rampa (nasce mais frio). Aceitável no MVP.</summary>
public sealed class WarmupState
{
    private readonly object _lock = new();
    private DateOnly _day;
    private readonly Dictionary<string, int> _sentToday = new(StringComparer.Ordinal);
    private readonly HashSet<string> _joinedGroups = new(StringComparer.Ordinal);

    // Quando a rampa começou (1ª vez que o motor rodou habilitado). Null = ainda não começou.
    public DateOnly? StartedOn { get; private set; }

    // Não inicia uma nova conversa antes deste instante (o gap aleatório entre conversas).
    public DateTimeOffset NextConversationAt { get; set; } = DateTimeOffset.MinValue;

    public void EnsureStarted(DateOnly today)
    {
        lock (_lock)
        {
            StartedOn ??= today;
        }
    }

    public int SentToday(string member, DateOnly today)
    {
        lock (_lock)
        {
            Roll(today);
            return _sentToday.GetValueOrDefault(member);
        }
    }

    public void RecordSent(string member, DateOnly today)
    {
        lock (_lock)
        {
            Roll(today);
            _sentToday[member] = _sentToday.GetValueOrDefault(member) + 1;
        }
    }

    public IReadOnlyDictionary<string, int> SnapshotSentToday(DateOnly today)
    {
        lock (_lock)
        {
            Roll(today);
            return new Dictionary<string, int>(_sentToday, StringComparer.Ordinal);
        }
    }

    public bool AlreadyJoined(string memberName, string inviteKey)
    {
        lock (_lock)
        {
            return _joinedGroups.Contains(Key(memberName, inviteKey));
        }
    }

    public void MarkJoined(string memberName, string inviteKey)
    {
        lock (_lock)
        {
            _joinedGroups.Add(Key(memberName, inviteKey));
        }
    }

    private void Roll(DateOnly today)
    {
        if (_day != today)
        {
            _day = today;
            _sentToday.Clear();
        }
    }

    private static string Key(string member, string invite) => member + "" + invite;
}
