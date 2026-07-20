using MtrxSys.Core.Domain.Common;

namespace MtrxSys.Core.Domain.Contacts;

public sealed class Contact : Entity<Guid>
{
    public PhoneNumber Phone { get; private set; } = null!;
    public string? Name { get; private set; }
    public string? GroupTag { get; private set; }
    /// <summary>Número (E164) do chip que estava CONECTADO quando este contato foi importado — ou seja,
    /// o chip que compartilha grupo com ele. O disparo só manda pros contatos do chip conectado no
    /// momento (co-membros dele); os de outros chips são frios → pulados (anti-463). Re-importar com
    /// outro chip "move" o contato pra ele. null = legado/sem marca (tratado como não-do-chip-atual).</summary>
    public string? ImportedByPhone { get; private set; }
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

    public static Contact Create(Guid id, PhoneNumber phone, string? name, string? groupTag, string? theme, DateTimeOffset? optInAt, string? importedByPhone = null)
    {
        return new Contact
        {
            Id = id,
            Phone = phone,
            Name = name,
            GroupTag = groupTag,
            Theme = theme,
            OptInAt = optInAt,
            ImportedByPhone = importedByPhone,
            Stage = ContactStage.Lead,
        };
    }

    public void RegisterSend(DateTimeOffset at) => LastSentAt = at;

    /// <summary>Garante que o contato conste como importado por ESTE chip (co-membro dele) — o gate-por-
    /// chip do disparo (anti-463) PULA quem não bate. Usado pelo aquecimento HÍBRIDO no Círculo: seus
    /// números são co-membros do próprio chip, mas os criados na Fase Humana nascem SEM ImportedByPhone
    /// (de propósito, pra o disparo os pular ANTES do híbrido). Retorna true se mudou. Não mexe em nada
    /// mais (diferente do ReimportInto, que também desfaz descarte/grupo).</summary>
    public bool EnsureImportedBy(string chipPhoneE164)
    {
        if (string.IsNullOrWhiteSpace(chipPhoneE164)
            || string.Equals(ImportedByPhone, chipPhoneE164, StringComparison.Ordinal))
        {
            return false;
        }
        ImportedByPhone = chipPhoneE164;
        return true;
    }

    /// <summary>Libera ESTE contato pra um novo disparo: zera o marcador de "já enviado"
    /// (LastSentAt) — equivalente per-contato do "Renovar lista". Como o filtro de disparo exclui
    /// quem tem LastSentAt, ele volta ao público. Não apaga histórico (o próximo disparo cria um
    /// job novo) e não mexe em Stage/OptOut. Retorna true se havia algo a limpar.</summary>
    public bool ClearLastSent()
    {
        if (LastSentAt is null)
        {
            return false;
        }
        LastSentAt = null;
        return true;
    }

    /// <summary>Descarta (soft delete) ESTE contato: some das listas, do disparo, do Chat e do
    /// resultado dos envios, mas a linha e o opt-out ficam no banco (anti-ban: o sync não recria e
    /// quem pediu "SAIR" continua suprimido). Reversível via ReimportInto. Retorna true se mudou.</summary>
    public bool Discard(DateTimeOffset at)
    {
        if (DeletedAt is not null)
        {
            return false;
        }
        DeletedAt = at;
        return true;
    }

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
    public bool ReimportInto(string? groupTag, string? importedByPhone = null)
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
        // Re-importar com um chip DIFERENTE "move" o contato pra ele: passa a ser co-membro DESTE chip
        // (que está no grupo agora), então o disparo deste chip pode mandar pra ele. É como o operador
        // "ativa" um contato antigo pro chip atual — só re-importar do grupo com o chip conectado.
        if (!string.IsNullOrWhiteSpace(importedByPhone) && !string.Equals(ImportedByPhone, importedByPhone, StringComparison.Ordinal))
        {
            ImportedByPhone = importedByPhone;
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
