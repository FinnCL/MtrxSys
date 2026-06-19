using Microsoft.Extensions.Logging;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.UseCases.Contacts;

public enum OptOutResult
{
    NotFound,
    AlreadyOptedOut,
    Marked,
}

/// <summary>
/// Marca um contato como opt-out a partir do seu Id — usado pelo link público /sair. Aplica a MESMA
/// transição do webhook (opt-out + stage "Descartado" + registro compartilhado), mas numa transação
/// SELF-CONTAINED (commit próprio), porque o /sair não está dentro do fluxo de ingestão de mensagem.
/// A confirmação ao destinatário NÃO é enviada por aqui: a própria página /sair já confirma.
/// </summary>
public sealed class MarkContactOptOutUseCase(
    IContactRepository contacts,
    IContactStageChangeRepository stageChanges,
    ISharedPhoneLedger ledger,
    IUnitOfWork uow,
    IClock clock,
    ILogger<MarkContactOptOutUseCase> log)
{
    public async Task<OptOutResult> MarkAsync(Guid contactId, CancellationToken ct)
    {
        var contact = await contacts.GetByIdAsync(contactId, ct);
        if (contact is null)
        {
            return OptOutResult.NotFound;
        }
        if (contact.OptOutAt is not null)
        {
            return OptOutResult.AlreadyOptedOut; // idempotente
        }

        var now = clock.UtcNow;
        contact.OptOut(now);
        var previous = contact.ChangeStage(ContactStage.Lost, now);
        await contacts.UpdateAsync(contact, ct);
        if (previous is { } from)
        {
            await stageChanges.AddAsync(
                ContactStageChange.Create(
                    id: Guid.NewGuid(),
                    contactId: contact.Id,
                    fromStage: from,
                    toStage: ContactStage.Lost,
                    changedAt: now,
                    changedByUserId: Guid.Empty),
                ct);
        }
        await uow.SaveChangesAsync(ct);
        log.LogInformation("Contato {ContactId} pediu opt-out via link", contact.Id);

        // Registro compartilhado (dedup cross-ambiente). Fail-open por contrato — após o commit local.
        await ledger.MarkOptOutAsync(contact.Phone.E164, ct);
        return OptOutResult.Marked;
    }
}
