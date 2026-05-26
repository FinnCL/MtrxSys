using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Contacts;

public sealed class Contact : Entity<Guid>
{
    public PhoneNumber Phone { get; private set; } = null!;
    public string? Name { get; private set; }
    public string? GroupTag { get; private set; }
    public string? Theme { get; private set; }
    public DateTimeOffset? OptInAt { get; private set; }
    public DateTimeOffset? OptOutAt { get; private set; }
    public DateTimeOffset? LastSentAt { get; private set; }
    public ContactStage Stage { get; private set; } = ContactStage.Lead;
    public DateTimeOffset? StageChangedAt { get; private set; }
    /// <summary>Soft delete ("descartado"): quando preenchido, some das listas e do disparo,
    /// mas a linha (e o opt-out) permanece no banco. null = ativo.</summary>
    public DateTimeOffset? DeletedAt { get; private set; }

    private Contact() { }

    public static Contact Create(Guid id, PhoneNumber phone, string? name, string? groupTag, string? theme, DateTimeOffset? optInAt)
    {
        return new Contact
        {
            Id = id,
            Phone = phone,
            Name = name,
            GroupTag = groupTag,
            Theme = theme,
            OptInAt = optInAt,
            Stage = ContactStage.Lead,
        };
    }

    public void RegisterSend(DateTimeOffset at) => LastSentAt = at;

    /// <summary>Preenche o nome só se ainda estiver vazio (ex.: backfill com o PushName de uma resposta).</summary>
    public void FillNameIfEmpty(string? name)
    {
        if (string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(name))
        {
            Name = name;
        }
    }

    public void OptOut(DateTimeOffset at) => OptOutAt = at;

    /// <summary>Re-importação de grupo: desfaz o descarte (soft delete) e preenche o grupo se
    /// estava sem. Não move entre grupos (só preenche quando vazio). Retorna true se algo mudou —
    /// é a forma de trazer de volta um contato descartado, que some da lista e não teria como
    /// reativar de outro jeito.</summary>
    public bool ReimportInto(string? groupTag)
    {
        var changed = false;
        if (DeletedAt is not null)
        {
            DeletedAt = null;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(GroupTag) && !string.IsNullOrWhiteSpace(groupTag))
        {
            GroupTag = groupTag;
            changed = true;
        }
        return changed;
    }

    /// <summary>
    /// Religa o contato: limpa o opt-out e volta pra "Novo" (Lead). Retorna o estágio
    /// anterior se mudou (pra registrar no histórico), ou null se já estava em "Novo".
    /// </summary>
    public ContactStage? Reactivate(DateTimeOffset at)
    {
        OptOutAt = null;
        return ChangeStage(ContactStage.Lead, at);
    }

    public ContactStage? ChangeStage(ContactStage newStage, DateTimeOffset at)
    {
        if (Stage == newStage)
        {
            return null;
        }
        var previous = Stage;
        Stage = newStage;
        StageChangedAt = at;
        return previous;
    }
}
