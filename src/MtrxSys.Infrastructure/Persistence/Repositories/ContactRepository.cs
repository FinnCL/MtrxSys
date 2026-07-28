using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;

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

    public async Task<IReadOnlyDictionary<string, Contact>> GetByPhoneDigitsAsync(
        IReadOnlyCollection<string> phoneDigits, CancellationToken ct)
    {
        if (phoneDigits.Count == 0)
        {
            return new Dictionary<string, Contact>(StringComparer.Ordinal);
        }
        var alvo = phoneDigits.ToHashSet(StringComparer.Ordinal);

        // Consulta direto a coluna GERADA phone_digits (via propriedade-sombra), que é o mesmo valor do
        // índice único: o Postgres resolve com Index Scan e devolve entidade RASTREADA (o xmin vem
        // certo, e o import muta esses contatos). Sem varredura, sem SQL cru, sem normalizar em memória.
        var found = await db.Contacts
            .Where(c => alvo.Contains(EF.Property<string>(c, "PhoneDigits")))
            .ToListAsync(ct);

        // Dedup por dígitos: dois formatos do mesmo número colapsam numa chave. Ativo tem prioridade
        // sobre descartado (é o que o chamador quer reusar), então descartado ordena por último.
        return found
            .OrderBy(c => c.DeletedAt != null)
            .GroupBy(c => PhoneDigits.Of(c.Phone.E164), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
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

    // SEM ApplyFilter de propósito: ele esconde descartado e opt-out, que é o certo pra disparo e o
    // errado pra "esse número já é conhecido?". AsNoTracking é seguro aqui porque é PROJEÇÃO — a
    // ressalva de não usá-lo no ListByFilterAsync vale pra entidade que o chamador muta.
    public async Task<IReadOnlyList<ContactPhoneStatus>> ListAllPhoneStatusAsync(CancellationToken ct) =>
        await db.Contacts.AsNoTracking()
            .Select(c => new ContactPhoneStatus(c.Phone.E164, c.DeletedAt == null && c.OptOutAt == null))
            .ToListAsync(ct);

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

    // "Renovar lista": zera o LastSentAt de quem tinha recebido, pra o selo voltar a "Novo"
    // junto com a fila zerada. Não toca em Stage/OptOut (respondeu/saiu continuam).
    // Uma instrução SQL em vez de materializar a base inteira: o caminho ingênuo (carregar todos os
    // contatos rastreados, mutar e salvar) custa memória e tempo proporcionais à base, e este método
    // existe justamente pra rodar sobre TODA ela. Mesmo padrão do ClearLastSentAsync logo abaixo.
    //
    // ⚠️ O `!=` sozinho NÃO pegaria os legados: em SQL, `NULL <> 'x'` é NULL (não é verdadeiro), então
    // contato sem marca — exatamente quem mais precisa migrar — ficaria de fora. Por isso o `== null`
    // explícito, sem depender de como o provedor traduz a comparação.
    public Task<int> ReassignToChipAsync(string chipPhoneE164, CancellationToken ct) =>
        db.Contacts
            .Where(c => c.DeletedAt == null
                && (c.ImportedByPhone == null || c.ImportedByPhone != chipPhoneE164))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ImportedByPhone, chipPhoneE164), ct);

    public Task<int> ClearLastSentAsync(CancellationToken ct) =>
        db.Contacts
            .Where(c => c.LastSentAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSentAt, (DateTimeOffset?)null), ct);

    // Aquecimento: libera SÓ os respondedores (Stage != Novo/Descartado, sem opt-out, não descartados)
    // pra re-disparar pros mesmos no dia seguinte. Frios ("Novo") NUNCA entram — a fase é só warm.
    public Task<int> ClearLastSentForEngagedAsync(CancellationToken ct) =>
        db.Contacts
            .Where(c => c.LastSentAt != null
                && c.DeletedAt == null
                && c.OptOutAt == null
                && !ContactStages.NonEngaged.Contains(c.Stage))
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSentAt, (DateTimeOffset?)null), ct);

    // Fase híbrida: libera SÓ os telefones do Círculo escolhido (não reabre frios que responderam).
    // Não toca em descartados/opt-out (defensivo — o círculo é curado, mas o guard não custa).
    public Task<int> ClearLastSentForPhonesAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct)
    {
        var arr = phonesE164?.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.Ordinal).ToArray()
            ?? [];
        return arr.Length == 0
            ? Task.FromResult(0)
            : db.Contacts
                .Where(c => c.LastSentAt != null
                    && c.DeletedAt == null
                    && c.OptOutAt == null
                    && arr.Contains(c.Phone.E164))
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.LastSentAt, (DateTimeOffset?)null), ct);
    }

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
        if (filter.ContactId is { } onlyContactId)
        {
            q = q.Where(c => c.Id == onlyContactId);
        }
        // Gate por chip na ORIGEM: o mesmo critério que o DispatchEngine aplica no envio
        // (`contact.ImportedByPhone == connectedPhone`, com Ordinal). Aqui ele evita ENFILEIRAR quem o
        // motor pularia depois. `IsNullOrEmpty` e não `is { }`: string vazia vinda de config/env não
        // pode virar um filtro que casa com ninguém — nesse caso vale a mesma regra do null (não filtra).
        if (!string.IsNullOrEmpty(filter.ImportedByPhone))
        {
            q = q.Where(c => c.ImportedByPhone == filter.ImportedByPhone);
        }
        // "Engajados" = qualquer um que respondeu/avançou: tudo menos "Novo" (Lead) e "Descartado" (Lost).
        if (filter.EngagedOnly)
        {
            q = q.Where(c => !ContactStages.NonEngaged.Contains(c.Stage));
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
            // Fora quem já recebeu (LastSentAt) OU já está na fila (Pending/Retrying) — o segundo é a
            // proteção contra o mesmo clique enfileirar a pessoa duas vezes. Failed (definitivo) fica
            // de fora de propósito, re-adicionável manualmente.
            q = q.Where(c => c.LastSentAt == null
                && !db.DispatchJobs.Any(j =>
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
