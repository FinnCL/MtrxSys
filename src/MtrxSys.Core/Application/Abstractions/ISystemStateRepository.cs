using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Core.Application.Abstractions;

public interface ISystemStateRepository
{
    Task<SystemStateAggregate> GetAsync(CancellationToken ct);
    Task UpdateAsync(SystemStateAggregate state, CancellationToken ct);

    // Lê só a flag de pausa manual, SEM passar pelo cache de tracking do GetAsync. O motor de
    // disparo roda um ciclo inteiro no mesmo DbContext, então GetAsync devolveria sempre a
    // cópia carregada na 1ª iteração — e nunca enxergaria o "Parar envios" que o operador
    // grava por outra requisição no meio do ciclo. Esta leitura vai fresca ao banco.
    Task<bool> IsManuallyPausedAsync(CancellationToken ct);
}
