using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Warmup;

namespace MtrxSys.Core.Application.UseCases.Contacts;

/// <summary>
/// Auto-inscreve no Círculo de Aquecimento os contatos do CADASTRO MANUAL introduzidos nos primeiros
/// dias do aquecimento do chip. Regra: nos primeiros <see cref="DispatchOptions.WarmingResponderOnlyDays"/>
/// dias de CALENDÁRIO a partir de <c>WarmupStartedOn</c>, todo contato novo entra no círculo (semente
/// re-enviável da fase híbrida); do dia seguinte (fase híbrida) em diante, NÃO entra automático — o
/// operador marca à mão se quiser.
/// <para>
/// ESCOPO DELIBERADO — chamado SÓ do cadastro MANUAL (<see cref="AddManualContactsUseCase"/>: números
/// que o operador digita/cola, tendem a ser SEUS/de confiança), NUNCA da importação de grupo (frios em
/// massa). Motivo: o Círculo é um pool de REENVIO DIÁRIO na fase híbrida — o HybridCycleEnqueuer
/// re-enfileira TODO o círculo todo dia e ele tem PRIORIDADE sobre o teto. Auto-enchê-lo com centenas de
/// frios importados seria reenvio diário pra frio (gatilho de ban) e entupiria o teto, sufocando o
/// crescimento nos frios novos. Por isso a semente automática é só a entrada manual.
/// </para>
/// <para>
/// É CALENDÁRIO e não "dias com envio": a fase "só respondeu" conta dias-ativos (chip parado não
/// amadurece), mas "introduzido nos 3 primeiros dias" é sobre QUANDO a pessoa entrou — dia corrido a
/// partir do marco. Usa a data de Brasília, igual ao marco (WarmupStartedOn é gravado em data BR).
/// Idempotente por telefone (índice único em warmup_circle). NÃO chama SaveChanges: o UoW do cadastro
/// persiste os contatos e os membros do círculo juntos, num commit só.
/// </para>
/// </summary>
public sealed class WarmupSeedEnroller(
    ISystemStateRepository systemState,
    IWarmupCircleRepository circle,
    IOptions<DispatchOptions> options,
    IClock clock)
{
    public async Task EnrollIfSeedPhaseAsync(
        IReadOnlyCollection<(string PhoneE164, string? Name)> newContacts, CancellationToken ct)
    {
        if (newContacts.Count == 0)
        {
            return;
        }
        var state = await systemState.GetAsync(ct);
        if (state?.WarmupStartedOn is not { } startedOn)
        {
            return; // sem estado/marco de aquecimento (chip não pareado/reconciliado) → não auto-inscreve
        }
        var window = Math.Max(0, options.Value.WarmingResponderOnlyDays);
        if (window == 0 || IClock.ToBrasiliaDate(clock.UtcNow) >= startedOn.AddDays(window))
        {
            return; // fora dos N primeiros dias (fase híbrida em diante) → não auto-inscreve
        }

        // O círculo é pequeno (seus números): 1 SELECT dos telefones já presentes evita N consultas
        // ponto-a-ponto num lote de importação grande. O HashSet também de-dupa repetidos do mesmo lote.
        var already = (await circle.ListAsync(ct))
            .Select(m => m.PhoneE164)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var (phone, name) in newContacts)
        {
            if (already.Add(phone))
            {
                await circle.AddAsync(
                    WarmupCircleMember.Create(Guid.NewGuid(), phone, name, clock.UtcNow), ct);
            }
        }
    }
}
