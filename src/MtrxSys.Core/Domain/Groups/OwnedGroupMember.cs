using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Groups;

/// <summary>Um telefone fotografado de dentro de um grupo criado aqui, no momento em que a isenção
/// de disparo foi ligada (ver <see cref="OwnedGroup.Members"/> pra o porquê de ser fotografia).
///
/// NÃO é um <c>Contact</c> e não tenta ser: aqui só interessa "este número está isento?". Ligar isto
/// ao domínio de contatos misturaria a lista de conhecidos do operador com o público de disparo.</summary>
public sealed class OwnedGroupMember : Entity<Guid>
{
    public Guid OwnedGroupId { get; private set; }

    /// <summary>E.164 com o '+', igual ao <c>Contact.Phone.E164</c> — é contra ele que a isenção é
    /// casada, e uma forma diferente aqui faria o casamento falhar em silêncio.</summary>
    public string PhoneE164 { get; private set; } = string.Empty;

    private OwnedGroupMember() { }

    public static OwnedGroupMember Create(Guid id, Guid ownedGroupId, string phoneE164)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phoneE164);
        return new OwnedGroupMember { Id = id, OwnedGroupId = ownedGroupId, PhoneE164 = phoneE164 };
    }
}
