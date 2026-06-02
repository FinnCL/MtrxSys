using Microsoft.Extensions.Logging;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Safety;

namespace MtrxSys.Core.Application.UseCases.Webhooks;

/// <summary>
/// Reconcilia opt-outs que o webhook não capturou — caso clássico: o destinatário respondeu
/// "Sair" enquanto o chip estava FORA, e a mensagem só entrou no histórico pelo sync (que NÃO
/// classifica). Varre as mensagens inbound dos contatos ainda ATIVOS e, batendo o
/// <see cref="OptOutDetector"/> (mesma regra do webhook), aplica opt-out + stage Lost com
/// auditoria — exatamente a transição do <see cref="WebhookIngestionService"/>.
///
/// NÃO reenvia confirmação (cortesia tardia, dias depois, seria ruído) — só corrige o status
/// pra a pessoa não voltar a receber. Isolado de propósito: não toca no webhook nem no sync.
/// </summary>
public sealed class OptOutReconciler(
    IContactRepository contacts,
    IConversationRepository conversations,
    IChatMessageRepository messages,
    IContactStageChangeRepository stageChanges,
    IUnitOfWork uow,
    IClock clock,
    ILogger<OptOutReconciler> log)
{
    // Respostas de saída são curtas e recentes; 200 msgs por conversa cobre com folga sem
    // carregar históricos enormes.
    private const int MessagesPerConversation = 200;

    public async Task<OptOutReconcileResult> ReconcileAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        // Só contatos ATIVOS e ainda não opt-out (ExcludeOptedOut=true também exclui descartados).
        var candidates = await contacts.ListByFilterAsync(new ContactFilter(ExcludeOptedOut: true), ct);
        var reconciled = new List<string>();

        foreach (var contact in candidates)
        {
            ct.ThrowIfCancellationRequested();
            if (contact.OptOutAt is not null)
            {
                continue; // defesa extra — o filtro já exclui
            }

            var conversation = await conversations.GetByContactIdAsync(contact.Id, ct);
            if (conversation is null)
            {
                continue;
            }

            var msgs = await messages.ListByConversationAsync(conversation.Id, MessagesPerConversation, 0, ct);
            var askedToLeave = msgs.Any(m =>
                m.Direction == MessageDirection.Inbound && OptOutDetector.IsOptOut(m.Body));
            if (!askedToLeave)
            {
                continue;
            }

            // Mesma transição do webhook: opt-out + Lost + auditoria de stage.
            contact.OptOut(now);
            await contacts.UpdateAsync(contact, ct);
            var previous = contact.ChangeStage(ContactStage.Lost, now);
            if (previous is not null)
            {
                await contacts.UpdateAsync(contact, ct);
                await stageChanges.AddAsync(
                    ContactStageChange.Create(
                        id: Guid.NewGuid(),
                        contactId: contact.Id,
                        fromStage: previous,
                        toStage: ContactStage.Lost,
                        changedAt: now,
                        changedByUserId: Guid.Empty),
                    ct);
            }

            reconciled.Add(contact.Phone.E164);
            log.LogInformation("Opt-out reconciliado (resposta de saída atrasada) para contato {ContactId}", contact.Id);
        }

        if (reconciled.Count > 0)
        {
            await uow.SaveChangesAsync(ct);
        }

        return new OptOutReconcileResult(reconciled.Count, reconciled);
    }
}

public sealed record OptOutReconcileResult(int Count, IReadOnlyList<string> Phones);
