using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Groups;

/// <summary>Um grupo que ESTE sistema criou — logo, do operador. Existe por um motivo simples: o
/// WAHA não diz quem criou um grupo. Não há campo `owner`; o mais próximo é o `role` do participante
/// ("superadmin"), que a doc não garante no NOWEB e que, de todo modo, confunde "fui promovido a
/// admin" com "eu criei". Adivinhar erraria em silêncio.
///
/// Então a fonte da verdade é o ATO: quem cria, sabe. Ao criar pelo botão, o id fica guardado aqui,
/// e a aba Grupos separa/destaca com certeza — sem heurística.
///
/// Consequência de desenho (não é regra a lembrar, é onde o dado mora): grupo que você entrou por
/// convite, ou em que te adicionaram, NUNCA terá registro aqui. Qualquer tratamento especial que
/// venha a existir só pode alcançar grupo criado por você.</summary>
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
}
