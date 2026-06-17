using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Core.Application.Abstractions;

public interface IGroupLinkRepository
{
    /// <summary>Carrega num único SELECT os links cujos códigos estão na lista, indexados por código.
    /// Usado na coleta pra deduplicar em lote (evita o N+1 de uma consulta por link captado).</summary>
    Task<IReadOnlyDictionary<string, GroupLink>> GetByCodesAsync(IReadOnlyCollection<string> codes, CancellationToken ct);
    Task<GroupLink?> GetByCodeAsync(string code, CancellationToken ct);
    Task AddAsync(GroupLink link, CancellationToken ct);
    Task UpdateAsync(GroupLink link, CancellationToken ct);
    /// <summary>Página da tela do Coletor. keyword filtra por nome do grupo OU palavra-chave de
    /// origem (ILIKE). status null = exclui os Invalid por padrão. Ordena por mais recente.</summary>
    Task<IReadOnlyList<GroupLink>> ListAsync(string? keyword, GroupLinkStatus? status, int limit, int offset, CancellationToken ct);

    /// <summary>Total de linhas que casam com o filtro (pra o front saber o número de páginas).</summary>
    Task<int> CountAsync(string? keyword, GroupLinkStatus? status, CancellationToken ct);

    /// <summary>Links ainda não resolvidos (Found), mais antigos primeiro, pra o enriquecimento
    /// drenar o backlog ao longo das coletas (não deixa link preso em "Resolvendo…").</summary>
    Task<IReadOnlyList<GroupLink>> ListForEnrichmentAsync(int limit, CancellationToken ct);

    /// <summary>Found ainda não resolvidos de um NICHO específico (matched_keyword), mais antigos
    /// primeiro — pra re-validar os achados daquele nicho a cada nova busca dele.</summary>
    Task<IReadOnlyList<GroupLink>> ListForEnrichmentByKeywordAsync(string keyword, int limit, CancellationToken ct);
}
