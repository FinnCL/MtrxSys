using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Funnel;

/// <summary>Um convite de funil: o link wa.me (click-to-chat) gerado pra um contato, mais a mensagem
/// que deve sair AUTOMATICAMENTE quando esse contato mandar o 1º inbound (a "concatenação": a pessoa
/// te escreve → o sistema dispara a mensagem-alvo). NÃO é um envio a frio (nada sai daqui até a pessoa
/// iniciar), então não passa pelo ledger anti-ban do disparo. `PrefillText` = texto pré-preenchido no
/// link; `AutoReplyText` = mensagem enviada no 1º inbound (vazio = sem auto-resposta).</summary>
public sealed class FunnelInvite : Entity<Guid>
{
    public Guid ContactId { get; private set; }
    public string? PrefillText { get; private set; }
    public string? AutoReplyText { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    /// <summary>Quando o contato respondeu (1º inbound observado por este convite). null = ainda pendente.</summary>
    public DateTimeOffset? EngagedAt { get; private set; }
    /// <summary>Quando a auto-resposta foi enviada. null = não enviada (sem AutoReplyText, ou ainda pendente).</summary>
    public DateTimeOffset? AutoRepliedAt { get; private set; }

    private FunnelInvite() { }

    public static FunnelInvite Create(Guid id, Guid contactId, string? prefillText, string? autoReplyText, DateTimeOffset createdAt)
    {
        return new FunnelInvite
        {
            Id = id,
            ContactId = contactId,
            PrefillText = prefillText,
            AutoReplyText = autoReplyText,
            CreatedAt = createdAt,
        };
    }

    /// <summary>Marca o engajamento (o contato respondeu). IDEMPOTENTE — só grava a 1ª vez.
    /// Retorna true se ESTE foi o 1º inbound deste convite (quem dispara a auto-resposta).</summary>
    public bool MarkEngaged(DateTimeOffset at)
    {
        if (EngagedAt is not null)
        {
            return false;
        }
        EngagedAt = at;
        return true;
    }

    public void MarkAutoReplied(DateTimeOffset at) => AutoRepliedAt ??= at;

    /// <summary>Tem auto-resposta pendente pra enviar? (texto configurado e ainda não enviada).</summary>
    public bool HasPendingAutoReply => !string.IsNullOrWhiteSpace(AutoReplyText) && AutoRepliedAt is null;
}
