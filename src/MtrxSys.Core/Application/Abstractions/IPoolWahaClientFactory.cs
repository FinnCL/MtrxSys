using MtrxSys.Core.Application.Options;

namespace MtrxSys.Core.Application.Abstractions;

/// <summary>Cria um <see cref="IWahaClient"/> apontado pro WAHA de UM membro do pool de aquecimento
/// (baseUrl + apiKey próprios). Necessário porque o <see cref="IWahaClient"/> do DI está preso ao
/// WAHA local (waha:3000); o motor de aquecimento precisa falar com o WAHA de CADA membro.
/// Implementado na Infra (reusa o WahaClient interno).</summary>
public interface IPoolWahaClientFactory
{
    IWahaClient CreateFor(WarmupMemberOptions member);
}
