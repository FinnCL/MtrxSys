using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Core.Application.Abstractions;

public interface IOwnedGroupRepository
{
    Task<IReadOnlyList<OwnedGroup>> ListAsync(CancellationToken ct);

    /// <summary>Os ids (forma do ListGroups) dos grupos criados por este sistema. É o que a listagem
    /// usa pra marcar `isMine` sem N consultas.</summary>
    Task<IReadOnlySet<string>> ListWaGroupIdsAsync(CancellationToken ct);

    Task<OwnedGroup?> GetByWaGroupIdAsync(string waGroupId, CancellationToken ct);

    /// <summary>Igual ao acima, mas RASTREADO e com os membros carregados — pra ligar/desligar a
    /// isenção. O GetByWaGroupIdAsync é AsNoTracking: mutar o que ele devolve não persiste nada, e o
    /// SaveChanges "passaria" sem gravar. Método separado pra que a escolha seja explícita.</summary>
    Task<OwnedGroup?> GetForUpdateAsync(string waGroupId, CancellationToken ct);

    /// <summary>Os ids dos grupos com a isenção LIGADA — pra a listagem mostrar o estado da chave sem
    /// N consultas. Distinto do de baixo: aqui é "quais GRUPOS estão ligados" (UI), lá é "quais
    /// TELEFONES estão isentos" (disparo).</summary>
    Task<IReadOnlySet<string>> ListExemptWaGroupIdsAsync(CancellationToken ct);

    /// <summary>Telefones isentos da trava de "já enviei pra esse": os membros fotografados dos
    /// grupos com a isenção LIGADA. Vazio quando nenhum grupo está ligado — que é o default, e o
    /// estado de toda a produção hoje. Lido 1x por ciclo do motor e 1x por enfileiramento.</summary>
    Task<IReadOnlySet<string>> ListExemptPhonesAsync(CancellationToken ct);

    Task AddAsync(OwnedGroup group, CancellationToken ct);

    /// <summary>Esquece que o grupo é nosso (o grupo em si continua no WhatsApp). Retorna false se
    /// já não havia registro — idempotente.</summary>
    Task<bool> RemoveAsync(string waGroupId, CancellationToken ct);
}
