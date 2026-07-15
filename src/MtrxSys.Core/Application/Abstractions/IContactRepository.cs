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
    // Telefones isentos da trava "já recebeu" do ExcludeAlreadyDispatched (membros de grupo criado
    // pelo operador com a isenção ligada — ver OwnedGroup). Isenta SÓ o LastSentAt: a trava de quem
    // já está na FILA (Pending/Retrying) continua valendo pra eles também, senão clicar "disparar"
    // duas vezes enfileiraria a mesma pessoa duas vezes — isso é proteção contra clique repetido,
    // não a regra de "conversa uma vez só". Vazio/null = nada isento (o default).
    IReadOnlyCollection<string>? ExemptPhonesE164 = null);

public sealed record ContactGroupTag(string GroupTag, int Count);
