using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Groups;

/// <summary>
/// Um link de convite de grupo (chat.whatsapp.com/&lt;código&gt;) captado de uma fonte (busca por
/// nicho, canal de Telegram ou entrada manual). O código é único — a dedup entre buscas é por ele.
/// </summary>
public sealed class GroupLink : Entity<Guid>
{
    public string InviteCode { get; private set; } = null!;
    public string InviteUrl { get; private set; } = null!;
    /// <summary>Origem do link (canal de Telegram, "searxng" ou "manual").</summary>
    public string? SourceChannel { get; private set; }
    /// <summary>Nome do grupo, lido da PÁGINA PÚBLICA do convite (og:title, sem entrar). null até resolver.</summary>
    public string? GroupName { get; private set; }
    public int? ParticipantCount { get; private set; }
    public GroupLinkStatus Status { get; private set; } = GroupLinkStatus.Found;
    /// <summary>Palavra-chave da busca que surfou este link (provênica; null no modo variado).</summary>
    public string? MatchedKeyword { get; private set; }
    /// <summary>JID/número do grupo no WhatsApp, preenchido ao entrar — usado pra importar.</summary>
    public string? WhatsAppGroupId { get; private set; }
    public DateTimeOffset CollectedAt { get; private set; }
    public DateTimeOffset? ResolvedAt { get; private set; }

    private GroupLink() { }

    public static GroupLink Create(
        Guid id, string inviteCode, string inviteUrl, string? sourceChannel, string? matchedKeyword, DateTimeOffset at)
    {
        return new GroupLink
        {
            Id = id,
            InviteCode = inviteCode,
            InviteUrl = inviteUrl,
            SourceChannel = sourceChannel,
            MatchedKeyword = string.IsNullOrWhiteSpace(matchedKeyword) ? null : matchedKeyword.Trim(),
            Status = GroupLinkStatus.Found,
            CollectedAt = at,
        };
    }

    /// <summary>Convite válido: grava nome/tamanho e passa a Resolved.</summary>
    public void Resolve(string? name, int? count, DateTimeOffset at)
    {
        GroupName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ParticipantCount = count;
        Status = GroupLinkStatus.Resolved;
        ResolvedAt = at;
    }

    /// <summary>Convite morto/expirado: marca Invalid (some das listas por padrão).</summary>
    public void MarkInvalid(DateTimeOffset at)
    {
        Status = GroupLinkStatus.Invalid;
        ResolvedAt = at;
    }

    /// <summary>Convite válido, mas o grupo aparenta não ser brasileiro: guarda nome/tamanho
    /// (pra inspeção via filtro) e marca Foreign (escondido por padrão).</summary>
    public void MarkForeign(string? name, int? count, DateTimeOffset at)
    {
        GroupName = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ParticipantCount = count;
        Status = GroupLinkStatus.Foreign;
        ResolvedAt = at;
    }

    /// <summary>Entrou no grupo (clique manual). Guarda o id do grupo no WhatsApp pra importar depois.</summary>
    public void MarkJoined(string? whatsAppGroupId)
    {
        if (!string.IsNullOrWhiteSpace(whatsAppGroupId))
        {
            WhatsAppGroupId = whatsAppGroupId;
        }
        Status = GroupLinkStatus.Joined;
    }

    public void MarkImported() => Status = GroupLinkStatus.Imported;

    /// <summary>Registra (ou atualiza) a palavra-chave que casou com este grupo.</summary>
    public void TagKeyword(string? keyword)
    {
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            MatchedKeyword = keyword.Trim();
        }
    }
}
