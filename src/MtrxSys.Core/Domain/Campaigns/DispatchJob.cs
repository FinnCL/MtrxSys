using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Campaigns;

/// <summary>Motivos de pulo que outro código precisa RECONHECER depois, e não só exibir.</summary>
public static class DispatchSkipReasons
{
    /// <summary>Pulo pelo gate anti-463 (contato de outro chip). É o único reversível: quando o contato
    /// é migrado para o chip conectado, o motivo deixa de existir e o job volta pra fila.
    /// <para>⚠️ O texto é comparado no banco para achar esses jobs. Mudá-lo faz os já gravados pararem
    /// de casar — se um dia precisar mudar, migre as linhas antigas junto.</para></summary>
    public const string OtherChip =
        "contato não é do chip conectado (só envia p/ importados por ele) — evita 463";
}

public sealed class DispatchJob : Entity<Guid>
{
    public Guid ContactId { get; private set; }
    public Guid TemplateId { get; private set; }
    public DispatchStatus Status { get; private set; }
    public DateTimeOffset ScheduledAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public string? WahaMessageId { get; private set; }
    public string? ErrorReason { get; private set; }

    // Quantas vezes este disparo já foi efetivamente tentado (incrementa a cada reenvio agendado).
    public int AttemptCount { get; private set; }

    private DispatchJob() { }

    public static DispatchJob Schedule(Guid id, Guid contactId, Guid templateId, DateTimeOffset scheduledAt)
    {
        return new DispatchJob
        {
            Id = id,
            ContactId = contactId,
            TemplateId = templateId,
            Status = DispatchStatus.Pending,
            ScheduledAt = scheduledAt,
        };
    }

    public void MarkSent(string wahaMessageId, DateTimeOffset at)
    {
        Status = DispatchStatus.Sent;
        WahaMessageId = wahaMessageId;
        SentAt = at;
    }

    public void MarkFailed(string reason, DateTimeOffset at)
    {
        Status = DispatchStatus.Failed;
        ErrorReason = reason;
        SentAt = at;
    }

    // Decide, no momento de uma falha, se ainda vale reenviar. AttemptCount é o nº de reenvios
    // já agendados (0 na 1ª falha). Contando a tentativa que acabou de falhar (AttemptCount+1),
    // ainda fica abaixo do teto total? Ex.: maxAttempts=2 → reenvia só quando AttemptCount==0.
    public bool CanRetry(int maxAttempts) => AttemptCount + 1 < maxAttempts;

    // Falha transitória: registra o motivo e reagenda pro fim da fila (nextAt = agora, mais novo
    // que os Pending antigos). Não mexe em SentAt — o disparo ainda não saiu de fato.
    public void ScheduleRetry(DateTimeOffset nextAt, string reason)
    {
        Status = DispatchStatus.Retrying;
        AttemptCount++;
        ScheduledAt = nextAt;
        ErrorReason = reason;
    }

    public void MarkSkipped(string reason)
    {
        Status = DispatchStatus.Skipped;
        ErrorReason = reason;
    }


    // Quantas vezes este disparo já foi ADIADO. Separado do AttemptCount de propósito: adiar não é
    // tentar enviar, e não pode queimar a tentativa (escassa, teto 2). Serve pra dar um FIM ao
    // adiamento — ver ExceededDeferLimit.
    public int DeferCount { get; private set; }

    // Adia o disparo pra frente (nextAt futuro) SEM consumir tentativa de envio e SEM marcar terminal.
    // Diferente de ScheduleRetry (que incrementa AttemptCount) e de MarkSkipped (terminal). Uso: a
    // checagem de número ficou indisponível por hiccup — não é falha do número nem do envio, então não
    // pode queimar o AttemptCount (escasso) nem perder o contato. Fica Retrying (segue sendo dequeuado
    // quando o nextAt chegar), pra o ciclo seguir com os outros e re-checar este depois.
    public void Defer(DateTimeOffset nextAt, string reason)
    {
        Status = DispatchStatus.Retrying;
        ScheduledAt = nextAt;
        ErrorReason = reason;
        DeferCount++;
    }

    /// <summary>Já adiou vezes demais? true = parar de adiar e resolver de outro jeito.</summary>
    /// <remarks>
    /// 🔴 Adiamento SEM FIM entope a fila, e isso é fácil de não enxergar porque cada volta parece
    /// inofensiva. O caso que motivou: no aparelho FÍSICO não há root, então não existe o `wa.db` que
    /// afirma "este número NÃO é usuário" — a checagem só sabe dizer "sim" ou "não sei". Um número que
    /// genuinamente não tem WhatsApp responde "não sei" para sempre, e o job voltava a cada ciclo
    /// consumindo o MESMO intervalo de envio que uma mensagem real consumiria. Com 10% de números
    /// ruins numa lista, a fila vai enchendo de jobs imortais que roubam os horários dos bons.
    /// <para>O limite não é para "desistir rápido": é para o silêncio ter um prazo. Ver
    /// DispatchOptions.MaxDeferrals.</para>
    /// </remarks>
    public bool ExceededDeferLimit(int maxDeferrals) => maxDeferrals > 0 && DeferCount >= maxDeferrals;
}
