using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Persistence.Repositories;

internal sealed class GroupLinkRepository(MtrxDbContext db) : IGroupLinkRepository
{
    public async Task<IReadOnlyDictionary<string, GroupLink>> GetByCodesAsync(
        IReadOnlyCollection<string> codes, CancellationToken ct)
    {
        if (codes.Count == 0)
        {
            return new Dictionary<string, GroupLink>(StringComparer.Ordinal);
        }
        var found = await db.GroupLinks
            .Where(x => codes.Contains(x.InviteCode))
            .ToListAsync(ct);
        // invite_code é único, sem colisão de chave.
        return found.ToDictionary(x => x.InviteCode, StringComparer.Ordinal);
    }

    public Task<GroupLink?> GetByCodeAsync(string code, CancellationToken ct) =>
        db.GroupLinks.FirstOrDefaultAsync(x => x.InviteCode == code, ct);

    public async Task AddAsync(GroupLink link, CancellationToken ct) =>
        await db.GroupLinks.AddAsync(link, ct);

    public Task UpdateAsync(GroupLink link, CancellationToken ct)
    {
        if (db.Entry(link).State == EntityState.Detached)
        {
            db.GroupLinks.Update(link);
        }
        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<GroupLink>> ListAsync(
        string? keyword, GroupLinkStatus? status, int limit, int offset, CancellationToken ct) =>
        await ApplyFilter(db.GroupLinks.AsQueryable(), keyword, status)
            // Grupos com mais participantes (ativos) primeiro; sem contagem vai pro fim; depois recentes.
            .OrderByDescending(x => x.ParticipantCount.HasValue)
            .ThenByDescending(x => x.ParticipantCount)
            .ThenByDescending(x => x.CollectedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

    public Task<int> CountAsync(string? keyword, GroupLinkStatus? status, CancellationToken ct) =>
        ApplyFilter(db.GroupLinks.AsQueryable(), keyword, status).CountAsync(ct);

    public async Task<IReadOnlyList<GroupLink>> ListForEnrichmentAsync(int limit, CancellationToken ct) =>
        await db.GroupLinks
            .Where(x => x.Status == GroupLinkStatus.Found)
            .OrderBy(x => x.CollectedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

    public async Task<IReadOnlyList<GroupLink>> ListForEnrichmentByKeywordAsync(string keyword, int limit, CancellationToken ct) =>
        await db.GroupLinks
            .Where(x => x.Status == GroupLinkStatus.Found && x.MatchedKeyword == keyword)
            .OrderBy(x => x.CollectedAt)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct);

    public Task<int> CountJoinedSinceAsync(DateTimeOffset sinceUtc, CancellationToken ct) =>
        db.GroupLinks.CountAsync(x => x.JoinedAt != null && x.JoinedAt >= sinceUtc, ct);

    public async Task<DateTimeOffset?> MaxJoinedAtAsync(CancellationToken ct) =>
        await db.GroupLinks.Where(x => x.JoinedAt != null).MaxAsync(x => (DateTimeOffset?)x.JoinedAt, ct);

    private static IQueryable<GroupLink> ApplyFilter(IQueryable<GroupLink> q, string? keyword, GroupLinkStatus? status)
    {
        if (status is { } s)
        {
            q = q.Where(x => x.Status == s);
        }
        else
        {
            // Por padrão, só os ÚTEIS: esconde os ainda-resolvendo (Found), os mortos (Invalid) e
            // os estrangeiros (Foreign). Sobram os validados (Resolved) e os acionados (Joined/Imported).
            q = q.Where(x => x.Status != GroupLinkStatus.Invalid
                && x.Status != GroupLinkStatus.Found
                && x.Status != GroupLinkStatus.Foreign);
        }
        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            q = q.Where(x =>
                (x.GroupName != null && EF.Functions.ILike(x.GroupName, pattern))
                || (x.MatchedKeyword != null && EF.Functions.ILike(x.MatchedKeyword, pattern)));
        }
        return q;
    }
}
