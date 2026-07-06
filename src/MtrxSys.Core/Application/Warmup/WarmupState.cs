namespace MtrxSys.Core.Application.Warmup;

/// <summary>Estado em memória do motor de aquecimento (singleton). Guarda a contagem diária por membro
/// (pra rampa), a agenda da próxima conversa (o gap) e os grupos já entrados (pra não reentrar). Reset
/// por dia (Brasília). Volátil (MVP): um restart reinicia a rampa (nasce mais frio → teto MENOR) mas
/// TAMBÉM zera a contagem do dia — logo, após um restart, o motor pode enviar mais um lote até o teto da
/// rampa naquele dia (o teto NÃO é persistente entre restarts). Aceitável porque o aquecimento é
/// atividade de baixo risco (mão dupla com os PRÓPRIOS números); persistir a contagem fica pra depois
/// se a cadência exigir.</summary>
public sealed class WarmupState
{
    private readonly object _lock = new();
    private DateOnly _day;
    private DateOnly? _startedOn;
    private readonly Dictionary<string, int> _sentToday = new(StringComparer.Ordinal);
    private readonly HashSet<string> _joinedGroups = new(StringComparer.Ordinal);

    // Quando a rampa começou (1ª vez que o motor rodou habilitado). Null = ainda não começou.
    // Leitura sob lock: o worker escreve (EnsureStarted) e o endpoint de status lê concorrente.
    public DateOnly? StartedOn
    {
        get { lock (_lock) { return _startedOn; } }
    }

    // Não inicia uma nova conversa antes deste instante (o gap aleatório entre conversas). Só o worker
    // (loop single-thread) lê/escreve — não precisa de lock.
    public DateTimeOffset NextConversationAt { get; set; } = DateTimeOffset.MinValue;

    public void EnsureStarted(DateOnly today)
    {
        lock (_lock)
        {
            _startedOn ??= today;
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

    // Separador '\n' (não aparece em nome/link) evita colisão de chave entre membro e convite.
    private static string Key(string member, string invite) => string.Concat(member, "\n", invite);
}
