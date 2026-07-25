using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class DispatchJobRepository(MtrxDbContext db) : IDispatchJobRepository
{
    public Task<DispatchJob?> DequeueNextPendingAsync(DateTimeOffset until, CancellationToken ct) =>
        db.DispatchJobs
            // TERMINA o que já começou antes de começar coisa nova: entre um Retrying JÁ VENCIDO e um
            // Pending, o vencido vem primeiro. Retrying é trabalho com investimento feito e JANELA —
            // no modo emulador o job é adiado depois de salvar o contato na agenda, esperando o
            // WhatsApp reconhecê-lo. Ordenando só por ScheduledAt, esse job caía atrás de TODA a fila
            // Pending (que costuma estar agendada no passado, no instante em que foi criada): com 125
            // contatos a 90-240s cada, o primeiro envio só aconteceria de 3 a 8 HORAS depois — o
            // preparo nunca se pagava e o operador via horas de "adiado" sem uma única mensagem.
            // Não starva a fila: o Retrying volta a vencer só depois da janela dele, e nesse intervalo
            // os Pending seguem sendo processados normalmente.
            .Where(j => (j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)
                && j.ScheduledAt <= until)
            .OrderBy(j => j.Status == DispatchStatus.Retrying ? 0 : 1)
            .ThenBy(j => j.ScheduledAt)
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

    // Fase de aquecimento: o relatório mostra SÓ respondedores (engajados) — o mesmo público que a fila
    // aceita nesses dias. Job de não-respondedor (pulado pelo motor, ou histórico legado) fica de fora,
    // e job órfão (sem contato) também sai (não é respondedor). Subquery correlacionada — mesmo padrão
    // do DeleteNonEngagedPending; a tradução EF é coberta por teste E2E.
    private IQueryable<DispatchJob> EngagedContactsOnly(IQueryable<DispatchJob> jobs) =>
        jobs.Where(j => db.Contacts.Any(c => c.Id == j.ContactId && !ContactStages.NonEngaged.Contains(c.Stage)));

    public async Task<DispatchStats> GetStatsAsync(string? currentChip, CancellationToken ct)
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
        // Fila do chip conectado agora (só esses saem — gate anti-463). Chip desconhecido → conta todos
        // (gate OFF, igual ao motor). Confiável: conta no banco, sem o cap/filtro do relatório paginado.
        var pendingFromCurrentChip = string.IsNullOrWhiteSpace(currentChip)
            ? pending + retrying
            : await ExcludingDiscardedContacts(db.DispatchJobs)
                .Where(j => (j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)
                    && db.Contacts.Any(c => c.Id == j.ContactId && c.ImportedByPhone == currentChip))
                .CountAsync(ct);
        return new DispatchStats(pending, sent, failed, skipped, retrying, pendingFromCurrentChip);
    }

    public async Task<IReadOnlyList<DispatchJob>> ListRecentAsync(int limit, CancellationToken ct) =>
        await db.DispatchJobs
            .OrderByDescending(j => j.SentAt ?? j.ScheduledAt)
            .Take(limit)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<DispatchReportItem>> ListReportAsync(DispatchStatus? status, int limit, bool engagedOnly, CancellationToken ct)
    {
        var baseQuery = db.DispatchJobs.AsQueryable();
        if (status is { } s)
        {
            baseQuery = baseQuery.Where(j => j.Status == s);
        }

        // Contatos descartados (soft delete) somem do resultado dos envios — mesmo filtro dos
        // contadores (ver ExcludingDiscardedContacts).
        baseQuery = ExcludingDiscardedContacts(baseQuery);

        // Fase de aquecimento: restringe a tabela a respondedores (ver EngagedContactsOnly).
        if (engagedOnly)
        {
            baseQuery = EngagedContactsOnly(baseQuery);
        }

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

    // Histórico (Enviada/Falhou/Pulada) de UM contato — some do relatório ao liberar pra novo disparo.
    // Mantém Pending/Retrying (fila ativa). ExecuteDelete roda fora do change tracker (bulk, sem xmin).
    public Task<int> DeleteHistoryByContactAsync(Guid contactId, CancellationToken ct) =>
        db.DispatchJobs
            .Where(j => j.ContactId == contactId
                && j.Status != DispatchStatus.Pending
                && j.Status != DispatchStatus.Retrying)
            .ExecuteDeleteAsync(ct);

    // Fase de aquecimento: apaga da FILA (Pending/Retrying) quem NÃO engajou (subquery correlacionada
    // no contato → EXISTS ... stage IN (...)). Só a fila; histórico fica. Traduzido por teste E2E.
    public Task<int> DeleteNonEngagedPendingAsync(CancellationToken ct) =>
        db.DispatchJobs
            .Where(j => (j.Status == DispatchStatus.Pending || j.Status == DispatchStatus.Retrying)
                && db.Contacts.Any(c => c.Id == j.ContactId && ContactStages.NonEngaged.Contains(c.Stage)))
            .ExecuteDeleteAsync(ct);

    // Reset diário do aquecimento: apaga o HISTÓRICO (não-fila) dos ENGAJADOS pra o dia começar limpo.
    // Engajado = NÃO está em NonEngaged (subquery correlacionada). A fila (Pending/Retrying) fica.
    public Task<int> DeleteEngagedHistoryAsync(CancellationToken ct) =>
        db.DispatchJobs
            .Where(j => j.Status != DispatchStatus.Pending
                && j.Status != DispatchStatus.Retrying
                && db.Contacts.Any(c => c.Id == j.ContactId && !ContactStages.NonEngaged.Contains(c.Stage)))
            .ExecuteDeleteAsync(ct);

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
                // Engajou = respondeu/avançou (fonte única ContactStages.IsEngaged) — a linha do
                // relatório mostra "Respondeu" pra esses, mesma definição do público "Respondeu".
                Engaged: c is not null && ContactStages.IsEngaged(c.Stage));
        }).ToList();
    }
}
