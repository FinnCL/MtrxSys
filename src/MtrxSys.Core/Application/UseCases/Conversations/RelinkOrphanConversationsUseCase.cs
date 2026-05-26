using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Validation;

namespace MtrxSys.Core.Application.UseCases.Conversations;

/// <summary>
/// Religa conversas individuais órfãs (sem contato) ao contato certo, resolvendo o telefone —
/// direto (@c.us) ou traduzindo o LID (@lid) via WAHA. Conserta conversas que nasceram sem
/// contato quando a sessão estava instável (o LID resolvia pra null naquele momento). Sem
/// vínculo, elas somem de TODAS as abas do chat (que filtram por contato != null).
/// É idempotente e seguro: só preenche o contact_id; não move mensagem nem apaga nada.
/// </summary>
public sealed class RelinkOrphanConversationsUseCase(
    IConversationRepository conversations,
    IContactRepository contacts,
    IWahaClient waha,
    IUnitOfWork uow,
    BrazilPhoneValidator phones,
    IOptions<DispatchOptions> dispatchOpts,
    ILogger<RelinkOrphanConversationsUseCase> log)
{
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var orphans = await conversations.ListUnlinkedIndividualAsync(ct);
        var linked = 0;
        foreach (var conv in orphans)
        {
            var e164 = await ResolvePhoneAsync(conv.WaChatId, ct);
            if (e164 is null)
            {
                continue;
            }
            var phone = phones.NormalizeTrusted(e164);
            var contact = await contacts.GetByPhoneAsync(phone.E164, ct);
            if (contact is null)
            {
                continue; // sem contato correspondente — não inventa
            }
            conv.LinkContact(contact.Id);
            await conversations.UpdateAsync(conv, ct);
            linked++;
        }
        if (linked > 0)
        {
            await uow.SaveChangesAsync(ct);
            log.LogInformation("Religadas {Count} conversa(s) órfã(s) ao contato.", linked);
        }
        return linked;
    }

    private async Task<string?> ResolvePhoneAsync(string chatId, CancellationToken ct)
    {
        var kind = WahaChatIdentifier.Classify(chatId);
        return kind switch
        {
            WahaChatIdentifier.Kind.Individual => WahaChatIdentifier.TryExtractPhoneE164(chatId),
            WahaChatIdentifier.Kind.LinkedId => await waha.ResolveLidToPhoneE164Async(dispatchOpts.Value.SessionId, chatId, ct),
            _ => null,
        };
    }
}
