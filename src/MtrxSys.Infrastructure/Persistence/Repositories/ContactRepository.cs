using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class ContactRepository(MtrxDbContext db) : IContactRepository
{
    public Task<Contact?> GetByIdAsync(Guid id, CancellationToken ct) =>
        db.Contacts.FirstOrDefaultAsync(c => c.Id == id, ct);

    public Task<Contact?> GetByPhoneAsync(string e164, CancellationToken ct) =>
        db.Contacts.FirstOrDefaultAsync(c => c.Phone.E164 == e164, ct);

    public async Task<IReadOnlyDictionary<string, Contact>> GetByPhonesAsync(
        IReadOnlyCollection<string> e164s, CancellationToken ct)
    {
        if (e164s.Count == 0)
        {
            return new Dictionary<string, Contact>(StringComparer.Ordinal);
        }
        var found = await db.Contacts
            .Where(c => e164s.Contains(c.Phone.E164))
            .ToListAsync(ct);
        // E.164 é único (índice único em phone_e164), então não há colisão de chave.
        return found.ToDictionary(c => c.Phone.E164, StringComparer.Ordinal);
    }

    public async Task AddAsync(Contact contact, CancellationToken ct) =>
        await db.Contacts.AddAsync(contact, ct);

    public Task UpdateAsync(Contact contact, CancellationToken ct)
    {
        if (db.Entry(contact).State == EntityState.Detached)
        {
            db.Contacts.Update(contact);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Contact>> ListByFilterAsync(ContactFilter filter, CancellationToken ct) =>
        await ApplyFilter(db.Contacts.AsQueryable(), filter).OrderBy(c => c.Phone.E164).ToListAsync(ct);

    // Só os telefones em opt-out (projeção — não materializa a entidade). Inclui descartados de
    // propósito: opt-out continua valendo (o número não pode ser disparado por nenhum chip).
    public async Task<IReadOnlyList<string>> ListOptedOutPhonesAsync(CancellationToken ct) =>
        await db.Contacts.AsNoTracking()
            .Where(c => c.OptOutAt != null)
            .Select(c => c.Phone.E164)
            .ToListAsync(ct);

    // Descarta (soft delete) os contatos de um grupo: marca deleted_at. Eles somem das listas e
    // do disparo (ApplyFilter/ListGroupTags filtram deleted_at IS NULL) e suas conversas somem do
    // Chat (ConversationRepository filtra pelo contato descartado) — mas a linha e o OPT-OUT ficam
    // no banco. Isso protege o anti-ban: o sync não recria o contato (GetByPhone o encontra) e
    // quem pediu "SAIR" continua suprimido. Reversível: basta zerar deleted_at.
    public async Task<int> DiscardByGroupTagAsync(string groupTag, DateTimeOffset now, CancellationToken ct) =>
        await db.Contacts
            .Where(c => c.GroupTag == groupTag && c.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, now), ct);

    // Purge SEGURO do grupo: apaga DE VEZ quem NÃO tem opt-out (com as filhas) e mantém quem deu
    // "SAIR" como soft delete (preserva a supressão — anti-ban/LGPD). Transacional: ou faz tudo, ou
    // nada. Retorna (apagados, preservados por opt-out).
    public async Task<(int Purged, int KeptOptedOut)> PurgeByGroupTagAsync(string groupTag, DateTimeOffset now, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Quem deu opt-out NÃO é apagado: vira soft delete (some da UI, mas o número segue suprimido).
        var keptOptedOut = await db.Contacts
            .Where(c => c.GroupTag == groupTag && c.OptOutAt != null && c.DeletedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedAt, now), ct);
        // Os demais (sem opt-out) são apagados fisicamente, junto das linhas-filhas.
        var purged = await HardDeleteContactsAsync(c => c.GroupTag == groupTag && c.OptOutAt == null, ct);
        await tx.CommitAsync(ct);
        return (purged, keptOptedOut);
    }

    // Apaga DE VEZ todos os contatos do grupo (inclusive opt-out) + filhas. Irreversível. Transacional.
    public async Task<int> HardDeleteByGroupTagAsync(string groupTag, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var deleted = await HardDeleteContactsAsync(c => c.GroupTag == groupTag, ct);
        await tx.CommitAsync(ct);
        return deleted;
    }

    // Núcleo do hard delete: apaga as filhas que NÃO caem por cascata de FK (dispatch_jobs e
    // conversations não têm FK pro contato → seriam órfãs) e depois os contatos. As chat_messages
    // caem por cascata ao apagar a conversation; contact_notes/stage_changes/tag_assignments caem
    // por cascata ao apagar o contato. Usa EXISTS no conjunto-alvo (subconsulta) — traduz sempre e
    // é set-based (sem lista de ids em memória). Os contatos são apagados por último, então o alvo
    // ainda existe quando as filhas são removidas. Deve rodar dentro de uma transação (ver chamadores).
    private async Task<int> HardDeleteContactsAsync(Expression<Func<Contact, bool>> match, CancellationToken ct)
    {
        var targets = db.Contacts.Where(match);
        await db.DispatchJobs.Where(j => targets.Any(c => c.Id == j.ContactId)).ExecuteDeleteAsync(ct);
        await db.Conversations.Where(cv => targets.Any(c => c.Id == cv.ContactId)).ExecuteDeleteAsync(ct);
        return await db.Contacts.Where(match).ExecuteDeleteAsync(ct);
    }

    // "Renovar lista": zera o LastSentAt de quem tinha recebido, pra o selo voltar a "Novo"
    // junto com a fila zerada. Não toca em Stage/OptOut (respondeu/saiu continuam).
    public Task<int> ClearLastSentAsync(CancellationToken ct) =>
        db.Contacts
            .Where(c => c.LastSentAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSentAt, (DateTimeOffset?)null), ct);

    public Task<int> CountByFilterAsync(ContactFilter filter, CancellationToken ct) =>
        ApplyFilter(db.Contacts.AsQueryable(), filter).CountAsync(ct);

    private IQueryable<Contact> ApplyFilter(IQueryable<Contact> q, ContactFilter filter)
    {
        // Descartados (soft delete) nunca aparecem em lista nem entram no público de disparo.
        q = q.Where(c => c.DeletedAt == null);
        if (filter.Stage is { } stage)
        {
            q = q.Where(c => c.Stage == stage);
        }
        // "Engajados" = qualquer um que respondeu/avançou: tudo menos "Novo" (Lead) e "Descartado" (Lost).
        if (filter.EngagedOnly)
        {
            q = q.Where(c => c.Stage != ContactStage.Lead && c.Stage != ContactStage.Lost);
        }
        if (filter.ExcludeOptedOut)
        {
            q = q.Where(c => c.OptOutAt == null);
        }
        // Não re-seleciona quem já recebeu disparo. Fonte de verdade do "já enviei" é o
        // LastSentAt — o MESMO marcador do selo "Não respondeu" —, então a exclusão bate com o
        // que aparece na tela mesmo que os jobs já tenham sido limpos. Inclui também quem está
        // na fila (Pending) OU em reenvio automático (Retrying) — senão um contato que falhou e
        // voltou pra fila apareceria de novo como "novo a adicionar". Falha DEFINITIVA (Failed)
        // fica de fora de propósito (re-adicionável manualmente). "Renovar lista" zera tudo.
        if (filter.ExcludeAlreadyDispatched)
        {
            q = q.Where(c => c.LastSentAt == null && !db.DispatchJobs.Any(j =>
                j.ContactId == c.Id
                && (j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)));
        }
        // Nunca dispara pro próprio número conectado (evita auto-envio).
        if (!string.IsNullOrWhiteSpace(filter.ExcludePhoneE164))
        {
            q = q.Where(c => c.Phone.E164 != filter.ExcludePhoneE164);
        }
        if (!string.IsNullOrWhiteSpace(filter.GroupTag))
        {
            q = q.Where(c => c.GroupTag == filter.GroupTag);
        }
        if (!string.IsNullOrWhiteSpace(filter.TagName))
        {
            var key = filter.TagName.Trim().ToLowerInvariant();
            var contactIds = db.ContactTagAssignments
                .Where(a => a.TagName == key)
                .Select(a => a.ContactId);
            q = q.Where(c => contactIds.Contains(c.Id));
        }
        return q;
    }

    public async Task<IReadOnlyList<ContactGroupTag>> ListGroupTagsAsync(CancellationToken ct)
    {
        // EF traduz o GroupBy numa projeção anônima; o mapeamento pro record e a
        // ordenação são feitos em memória (EF não traduz projeção via construtor + OrderBy).
        var raw = await db.Contacts
            .Where(c => c.GroupTag != null && c.DeletedAt == null)
            .GroupBy(c => c.GroupTag!)
            .Select(g => new { Tag = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return raw
            .OrderBy(x => x.Tag, StringComparer.OrdinalIgnoreCase)
            .Select(x => new ContactGroupTag(x.Tag, x.Count))
            .ToList();
    }
}
