using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Core.Application.Abstractions;

public interface IOwnedGroupRepository
{
    /// <summary>As marcas de posse, por grupo: id (na forma do ListGroups) → isenção ligada.
    /// Estar na chave = o grupo é meu; o valor = a chave de envio repetido.
    ///
    /// UMA leitura, não duas: a listagem pergunta "é meu?" E "está isento?" das MESMAS linhas, a
    /// cada abertura da aba.
    ///
    /// Distinto do <see cref="ListExemptPhonesAsync"/>: aqui é "quais GRUPOS" (a tela), lá é "quais
    /// TELEFONES" (o disparo).</summary>
    Task<IReadOnlyDictionary<string, bool>> ListOwnershipMarksAsync(CancellationToken ct);

    Task<OwnedGroup?> GetByWaGroupIdAsync(string waGroupId, CancellationToken ct);

    /// <summary>Igual ao acima, mas RASTREADO e com os membros carregados — pra ligar/desligar a
    /// isenção. O GetByWaGroupIdAsync é AsNoTracking: mutar o que ele devolve não persiste nada, e o
    /// SaveChanges "passaria" sem gravar. Método separado pra que a escolha seja explícita.</summary>
    Task<OwnedGroup?> GetForUpdateAsync(string waGroupId, CancellationToken ct);

    /// <summary>Telefones isentos da trava de "já enviei pra esse": os membros fotografados dos
    /// grupos com a isenção LIGADA. Vazio quando nenhum grupo está ligado — que é o default, e o
    /// estado de toda a produção hoje. Lido 1x por ciclo do motor e 1x por enfileiramento.</summary>
    Task<IReadOnlySet<string>> ListExemptPhonesAsync(CancellationToken ct);

    Task AddAsync(OwnedGroup group, CancellationToken ct);

    /// <summary>Esquece que o grupo é nosso (o grupo em si continua no WhatsApp). Retorna false se
    /// já não havia registro — idempotente.</summary>
    Task<bool> RemoveAsync(string waGroupId, CancellationToken ct);
}
