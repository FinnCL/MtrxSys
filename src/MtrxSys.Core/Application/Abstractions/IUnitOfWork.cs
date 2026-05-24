namespace MtrxSys.Core.Application.Abstractions;

public interface IUnitOfWork
{
    Task<int> SaveChangesAsync(CancellationToken ct);

    /// <summary>Descarta as alterações ainda não persistidas (entidades rastreadas em memória),
    /// sem tocar no que já foi commitado. Usado para isolar uma operação de "melhor-esforço"
    /// que falhou, de modo que suas alterações pendentes não contaminem o próximo SaveChanges.</summary>
    void DiscardChanges();
}
