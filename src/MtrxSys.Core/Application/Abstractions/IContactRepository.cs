using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Core.Application.Abstractions;

public interface IContactRepository
{
    Task<Contact?> GetByIdAsync(Guid id, CancellationToken ct);
    Task<Contact?> GetByPhoneAsync(string e164, CancellationToken ct);
    /// <summary>Carrega num único SELECT os contatos cujos telefones estão na lista, indexados por
    /// E.164. Usado pela importação de grupo pra evitar o N+1 (uma consulta por participante).</summary>
    Task<IReadOnlyDictionary<string, Contact>> GetByPhonesAsync(IReadOnlyCollection<string> e164s, CancellationToken ct);
    Task AddAsync(Contact contact, CancellationToken ct);
    Task UpdateAsync(Contact contact, CancellationToken ct);
    Task<IReadOnlyList<Contact>> ListByFilterAsync(ContactFilter filter, CancellationToken ct);
    Task<int> CountByFilterAsync(ContactFilter filter, CancellationToken ct);
    /// <summary>Telefones (E.164) de TODOS os contatos em opt-out (incluindo descartados — opt-out
    /// continua valendo). Projeção leve para o backfill periódico do registro compartilhado, sem
    /// carregar entidades inteiras.</summary>
    Task<IReadOnlyList<string>> ListOptedOutPhonesAsync(CancellationToken ct);
    Task<IReadOnlyList<ContactGroupTag>> ListGroupTagsAsync(CancellationToken ct);
    /// <summary>Descarta (soft delete) os contatos de um grupo: marca deleted_at, somem das
    /// listas/disparo, mas a linha e o opt-out ficam no banco. Retorna quantos foram descartados.</summary>
    Task<int> DiscardByGroupTagAsync(string groupTag, DateTimeOffset now, CancellationToken ct);

    /// <summary>Zera o marcador de envio (LastSentAt) de todos os contatos. Usado no "Renovar
    /// lista": quem só tinha recebido volta a "Novo", consistente com voltar a ser re-disparável.</summary>
    Task<int> ClearLastSentAsync(CancellationToken ct);

    /// <summary>Passa o "dono" (ImportedByPhone) de TODOS os contatos vivos para o chip informado, numa
    /// única instrução. Retorna quantos mudaram de fato.
    /// <para>⚠️ Afrouxa a trava anti-463 por decisão explícita do operador — ver o endpoint
    /// <c>/api/contacts/reassign-to-current-chip</c>. Não chamar de nada automático.</para></summary>
    Task<int> ReassignToChipAsync(string chipPhoneE164, CancellationToken ct);

    /// <summary>Zera o LastSentAt só de quem ENGAJOU (respondeu/avançou — Stage != Novo/Descartado), pra
    /// re-disparar pros MESMOS no dia seguinte durante o aquecimento. NÃO toca em quem saiu (opt-out),
    /// descartados, nem nos "Novo" (frios). Retorna quantos foram liberados.</summary>
    Task<int> ClearLastSentForEngagedAsync(CancellationToken ct);

    /// <summary>Zera o LastSentAt SÓ dos telefones dados (o Círculo de Aquecimento escolhido pelo
    /// operador), pra re-disparar pros MESMOS na fase híbrida — SEM reabrir frios que responderam
    /// (esses ficam com LastSentAt e o dedup os mantém fora). Retorna quantos foram liberados.</summary>
    Task<int> ClearLastSentForPhonesAsync(IReadOnlyCollection<string> phonesE164, CancellationToken ct);
}

public sealed record ContactFilter(
    ContactStage? Stage = null,
    string? TagName = null,
    string? GroupTag = null,
    bool ExcludeOptedOut = true,
    bool EngagedOnly = false,
    // Telefone E.164 a excluir — usado pra nunca disparar pro próprio número conectado.
    string? ExcludePhoneE164 = null,
    // Exclui quem já tem job Pending ou Sent — evita re-enviar pra quem já recebeu e
    // duplicar quem já está na fila. Usado no disparo e na prévia de público.
    bool ExcludeAlreadyDispatched = false,
    // Restringe a UM contato (por Id). Usado pelo enfileiramento por-respondedor (webhook) pra
    // reusar EXATAMENTE o mesmo dedup/segurança do disparo em lote, só que num contato só.
    Guid? ContactId = null);

public sealed record ContactGroupTag(string GroupTag, int Count);
