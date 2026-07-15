using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Groups;

/// <summary>Um grupo que o operador declarou ser dele. Existe por um motivo simples: o WAHA não diz
/// quem criou um grupo. Não há campo `owner`; o mais próximo é o `role` do participante
/// ("superadmin"), que a doc não garante no NOWEB e que, de todo modo, confunde "fui promovido a
/// admin" com "eu criei". Adivinhar erraria em silêncio — daí um registro explícito.
///
/// A posse entra aqui por DOIS caminhos, e o principal é o segundo:
///   1. o grupo é criado pelo sistema (o ato de criar já é a prova);
///   2. o operador DECLARA que um grupo existente é dele ("Este grupo é meu").
///
/// O (2) não é um remendo do (1) — é o caso normal. O grupo de aquecimento nasce no APARELHO
/// FÍSICO, na mão: num chip novo e frio, ter "criar grupo com 5 participantes" via API como
/// primeira atividade da conta é assinatura de bot, exatamente o que a fase humana existe pra
/// evitar. Então o sistema não pode exigir ter criado o grupo pra reconhecê-lo.
///
/// O QUE ISSO CUSTA, e é preciso ter claro: enquanto só o (1) existia, a isenção de disparo não
/// TINHA COMO alcançar um grupo de terceiros — era impossível por construção. Com o (2), ela alcança
/// qualquer grupo que o operador marcar, e marcar o grupo errado (um de leads frios) liberaria
/// re-envio pra desconhecidos. A proteção deixou de ser estrutural e virou responsabilidade de quem
/// marca. O que sobra de estrutural: a isenção é por grupo, exige um segundo ato explícito, nasce
/// desligada, e nunca dispensa opt-out.</summary>
public sealed class OwnedGroup : Entity<Guid>
{
    /// <summary>Id do grupo no WhatsApp, na MESMA forma que o ListGroups devolve: o número ANTES do
    /// '@' (ver WahaParsing.MapNowebGroup / ReadCreatedGroupId). Guardar o JID completo aqui faria o
    /// casamento com a listagem falhar em silêncio — o grupo simplesmente não apareceria como seu.</summary>
    public string WaGroupId { get; private set; } = string.Empty;

    /// <summary>Nome no momento da criação. É só rótulo/histórico: o nome VIVO vem do WAHA na
    /// listagem (o operador pode renomear pelo celular, e aí este campo fica velho de propósito —
    /// não vale sincronizar o que não é fonte da verdade).</summary>
    public string Name { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Ligado: quem está em <see cref="Members"/> pode receber disparo MAIS DE UMA VEZ — a
    /// trava de "já enviei pra esse" não vale pra eles. Nasce DESLIGADO, e é por grupo: a isenção
    /// nunca alcança contato fora de um grupo criado aqui.
    ///
    /// O que a isenção NÃO afeta, por decisão: o OPT-OUT (quem deu SAIR fica fora, sempre — inclusive
    /// o opt-out registrado em outro ambiente), a checagem de número existente (protege o chip do
    /// 463), o teto diário e a fase humana. Ela dispensa a trava de "conversa só uma vez", não as
    /// travas que existem pra não queimar o chip nem para respeitar quem pediu pra sair.</summary>
    public bool DispatchExemptionEnabled { get; private set; }

    /// <summary>Telefones isentos: FOTOGRAFIA dos membros tirada quando a isenção foi ligada, não
    /// leitura viva do WAHA. É de propósito — o disparo consultaria o WAHA a cada envio, e um hiccup
    /// decidiria em silêncio quem é isento (fail-open = manda de novo pra quem não devia; fail-closed
    /// = a isenção some sem aviso). Entrou gente no grupo depois? Desligar e religar re-fotografa.</summary>
    public IReadOnlyCollection<OwnedGroupMember> Members => members;
    private readonly List<OwnedGroupMember> members = [];

    private OwnedGroup() { }

    public static OwnedGroup Create(Guid id, string waGroupId, string name, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(waGroupId);
        return new OwnedGroup
        {
            Id = id,
            WaGroupId = waGroupId,
            Name = name,
            CreatedAt = createdAt,
        };
    }

    /// <summary>Liga a isenção JUNTO com a fotografia dos membros — de propósito, num método só: uma
    /// chave ligada sobre lista vazia (ou velha) diria "isento" sem isentar ninguém, ou isentaria
    /// quem já saiu do grupo.</summary>
    public void EnableDispatchExemption(IEnumerable<string> memberPhonesE164, Func<Guid> newId)
    {
        members.Clear();
        foreach (var phone in memberPhonesE164.Distinct(StringComparer.Ordinal))
        {
            members.Add(OwnedGroupMember.Create(newId(), Id, phone));
        }
        DispatchExemptionEnabled = true;
    }

    /// <summary>Desliga e ESQUECE a fotografia: guardá-la deixaria uma lista velha pronta pra voltar
    /// a valer no religar sem ninguém ter conferido quem está no grupo hoje.</summary>
    public void DisableDispatchExemption()
    {
        members.Clear();
        DispatchExemptionEnabled = false;
    }
}
