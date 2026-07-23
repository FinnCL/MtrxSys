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
    ISharedPhoneLedger ledger,
    ILogger<OptOutReconciler> log)
{
    // Contatos por ida ao banco atrás das mensagens. Segura o pico de memória sem voltar ao N+1.
    private const int ConversationsPerBatch = 500;

    public async Task<OptOutReconcileResult> ReconcileAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        // Só contatos ATIVOS e ainda não opt-out (ExcludeOptedOut=true também exclui descartados).
        var candidates = await contacts.ListByFilterAsync(new ContactFilter(ExcludeOptedOut: true), ct);
        if (candidates.Count == 0)
        {
            return new OptOutReconcileResult(0, []);
        }

        // DUAS consultas + uma por lote de conversas, em vez de DUAS POR CONTATO (conversa +
        // mensagens). Numa base de milhares de contatos ativos — e a maioria nunca responde, então
        // quase toda ida ao banco voltava vazia — aquilo era milhares de round-trips a cada ciclo do
        // auto-sync que importasse qualquer mensagem.
        var chosen = (await conversations.ListIndividualByContactIdsAsync([.. candidates.Select(c => c.Id)], ct))
            .GroupBy(c => c.ContactId)
            // MESMO critério do GetByContactIdAsync (a de atividade mais recente): um contato pode ter
            // mais de uma conversa (@c.us e @lid), e trocar a escolhida mudaria o que é detectado.
            .ToDictionary(g => g.Key, g => g.OrderByDescending(c => c.LastActivityAt).First().ConversationId);

        // Só os contatos que TÊM conversa entram no varejo das mensagens (o OptOutAt é defesa extra:
        // o filtro da consulta já exclui quem saiu).
        var withConversation = candidates
            .Where(c => c.OptOutAt is null && chosen.ContainsKey(c.Id))
            .Select(c => (Contact: c, ConversationId: chosen[c.Id]));

        var reconciled = new List<string>();

        // Em LOTES: pedir o inbound de TODAS as conversas de uma vez faria o pico de memória crescer
        // com a base (o loop antigo, apesar de lento, só segurava uma conversa por vez). Assim ficam
        // poucas consultas E memória limitada — uma query a cada ConversationsPerBatch contatos.
        foreach (var batch in withConversation.Chunk(ConversationsPerBatch))
        {
            ct.ThrowIfCancellationRequested();
            var inbound = (await messages.ListInboundByConversationsAsync(
                    [.. batch.Select(x => x.ConversationId)], ct))
                .ToLookup(m => m.ConversationId);

            foreach (var (contact, conversationId) in batch)
            {
                // Mensagem ANTERIOR à reativação manual não conta: o operador já viu aquele "sair" e
                // religou o contato de propósito. Sem este corte o reconciliador reencontrava a mesma
                // mensagem no histórico e devolvia o contato pra "Saiu" — na prática o botão
                // "Reativar" não funcionava, o status voltava sozinho no ciclo seguinte do auto-sync.
                // Um "sair" NOVO (depois da reativação) continua sendo respeitado.
                var askedToLeave = inbound[conversationId].Any(m =>
                    (contact.ReactivatedAt is null || m.Timestamp > contact.ReactivatedAt)
                    && OptOutDetector.IsOptOut(m.Body));
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
        }

        if (reconciled.Count > 0)
        {
            await uow.SaveChangesAsync(ct);
            // Opt-out global: propaga os reconciliados pro registro compartilhado (após o commit
            // local). Fail-open e idempotente — uma falha aqui não desfaz a reconciliação local.
            foreach (var phone in reconciled)
            {
                await ledger.MarkOptOutAsync(phone, ct);
            }
        }

        return new OptOutReconcileResult(reconciled.Count, reconciled);
    }
}

public sealed record OptOutReconcileResult(int Count, IReadOnlyList<string> Phones);
