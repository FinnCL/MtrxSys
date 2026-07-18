using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class DispatchJobRepository(MtrxDbContext db) : IDispatchJobRepository
{
    public Task<DispatchJob?> DequeueNextPendingAsync(DateTimeOffset until, CancellationToken ct) =>
        db.DispatchJobs
            // Retrying = falhou e voltou pro fim da fila; é processado igual a Pending. Como o
            // reenvio grava ScheduledAt = agora, ele naturalmente sai depois dos Pending antigos.
            .Where(j => (j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)
                && j.ScheduledAt <= until)
            .OrderBy(j => j.ScheduledAt)
            .FirstOrDefaultAsync(ct);

    public async Task<DateTimeOffset?> GetEarliestPendingScheduledAtAsync(CancellationToken ct) =>
        await db.DispatchJobs
            .Where(j => j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)
            .Select(j => (DateTimeOffset?)j.ScheduledAt)
            .MinAsync(ct);

    public async Task AddAsync(DispatchJob job, CancellationToken ct) =>
        await db.DispatchJobs.AddAsync(job, ct);

    public Task UpdateAsync(DispatchJob job, CancellationToken ct)
    {
        if (db.Entry(job).State == EntityState.Detached)
        {
            db.DispatchJobs.Update(job);
        }
        return Task.CompletedTask;
    }

    // Exclui os jobs de contatos descartados (soft delete) do relatório E dos contadores — igual às
    // listas e ao Chat. Filtra pelo JOB (não pelo join do contato) pra a linha inteira sair, em vez de
    // virar linha com telefone/nome em branco; o NOT-EXISTS-descartado preserva jobs órfãos (sem
    // contato correspondente), que aparecem como antes. Reversível: reativar/re-importar o contato
    // (zera DeletedAt) traz os jobs dele de volta.
    private IQueryable<DispatchJob> ExcludingDiscardedContacts(IQueryable<DispatchJob> jobs) =>
        jobs.Where(j => !db.Contacts.Any(c => c.Id == j.ContactId && c.DeletedAt != null));

    public async Task<DispatchStats> GetStatsAsync(CancellationToken ct)
    {
        var grouped = await ExcludingDiscardedContacts(db.DispatchJobs)
            .GroupBy(j => j.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var pending = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Pending)?.Count ?? 0;
        var sent = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Sent)?.Count ?? 0;
        var failed = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Failed)?.Count ?? 0;
        var skipped = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Skipped)?.Count ?? 0;
        var retrying = grouped.FirstOrDefault(g => g.Status == DispatchStatus.Retrying)?.Count ?? 0;
        return new DispatchStats(pending, sent, failed, skipped, retrying);
    }

    public async Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct) =>
        await db.DispatchJobs
            .OrderByDescending(j => j.SentAt ?? j.ScheduledAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DispatchReportItem>> ListReportAsync(DispatchStatus? status, int limit, CancellationToken ct)
    {
        var baseQuery = db.DispatchJobs.AsQueryable();
        if (status is { } s)
        {
            baseQuery = baseQuery.Where(j => j.Status == s);
        }

        // Contatos descartados (soft delete) somem do resultado dos envios — mesmo filtro dos
        // contadores (ver ExcludingDiscardedContacts).
        baseQuery = ExcludingDiscardedContacts(baseQuery);

        // Duas queries pra ordenar cada grupo na direção certa — não dá pra fazer em uma só
        // porque histórico precisa de DESC e Pending precisa de ASC.
        //
        // Histórico (Sent/Failed/Skipped) no topo, mais recente primeiro: o operador vê a
        // última atividade direto.
        var historyJobs = await baseQuery
            .Where(j => j.Status != DispatchStatus.Pending && j.Status != DispatchStatus.Retrying)
            .OrderByDescending(j => j.SentAt ?? j.ScheduledAt)
            .Take(limit)
            .ToListAsync(ct);

        // Pending no fim, em ordem FIFO (ScheduledAt ASC) — espelha exatamente como o
        // dispatcher vai processar (DequeueNextPendingAsync usa OrderBy(ScheduledAt) ASC).
        // Sem isso, um contato recém adicionado aparecia ACIMA de Pendings mais antigos
        // (DESC mostrava o último primeiro), dando a sensação de desorganização quando o
        // operador clicava "Iniciar envios" e a fila era consumida em ordem diferente da
        // exibida.
        var remaining = Math.Max(0, limit - historyJobs.Count);
        var pendingJobs = await baseQuery
            .Where(j => j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)
            .OrderBy(j => j.ScheduledAt)
            .Take(remaining)
            .ToListAsync(ct);

        // Histórico no topo, fila Pending no fim — ordem de exibição final.
        var jobs = historyJobs.Concat(pendingJobs).ToList();

        // Carrega os contatos referenciados num lote só e mapeia em memória
        // (evita join de tipo owned + left join, que o EF traduz mal).
        return await BuildReportAsync(jobs, ct);
    }

    // "Na fila" = Pending E Retrying (este último falhou uma vez e voltou pra fila). Ambos ainda
    // sairiam — então limpar a fila precisa remover os dois, senão um Retrying escaparia da limpeza.
    public Task<int> ClearPendingAsync(CancellationToken ct) =>
        db.DispatchJobs
            .Where(j => j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)
            .ExecuteDeleteAsync(ct);

    public Task<int> ClearPendingByTemplateAsync(Guid templateId, CancellationToken ct) =>
        db.DispatchJobs
            .Where(j => j.TemplateId == templateId
                && (j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying))
            .ExecuteDeleteAsync(ct);

    public Task<int> ClearAllAsync(CancellationToken ct) =>
        db.DispatchJobs.ExecuteDeleteAsync(ct);

    private async Task<IReadOnlyList<DispatchReportItem>> BuildReportAsync(List<DispatchJob> jobs, CancellationToken ct)
    {
        var contactIds = jobs.Select(j => j.ContactId).Distinct().ToList();
        var contacts = await db.Contacts
            .Where(c => contactIds.Contains(c.Id))
            .ToListAsync(ct);
        var byId = contacts.ToDictionary(c => c.Id);

        return jobs.Select(j =>
        {
            byId.TryGetValue(j.ContactId, out var c);
            return new DispatchReportItem(
                Phone: c?.Phone.E164,
                Name: c?.Name,
                Status: j.Status.ToString(),
                ScheduledAt: j.ScheduledAt,
                SentAt: j.SentAt,
                ErrorReason: j.ErrorReason,
                AttemptCount: j.AttemptCount,
                ImportedByPhone: c?.ImportedByPhone,
                // Engajou = respondeu/avançou (Stage != Novo/Descartado). Mesma definição do público
                // "Respondeu" do disparo — a linha do relatório mostra "Respondeu" pra esses.
                Engaged: c is not null && c.Stage != ContactStage.Lead && c.Stage != ContactStage.Lost);
        }).ToList();
    }
}
