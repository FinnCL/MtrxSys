using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>
/// Fonte de busca com RESERVA: tenta o motor primário (Serper, quando há chave) e, se ele não
/// trouxer nada (cota esgotada, 429/401, erro), CAI no secundário (SearXNG, grátis). Assim a busca
/// por nicho não morre quando o crédito do Serper acaba — degrada do pago pro grátis sozinha. Se o
/// primário não está configurado, usa direto o secundário.
/// </summary>
internal sealed class CompositeSearchSource(
    IGroupLinkSearchSource primary,
    IGroupLinkSearchSource secondary,
    ISearchStatus status) : IGroupLinkSearchSource
{
    public string Engine => primary.IsConfigured ? primary.Engine : secondary.Engine;

    public bool IsConfigured => primary.IsConfigured || secondary.IsConfigured;

    public async Task<IReadOnlyList<RawGroupLink>> SearchAsync(string keyword, int maxResults, CancellationToken ct)
    {
        // Sem primário → só o secundário (ou vazio).
        if (!primary.IsConfigured)
        {
            return secondary.IsConfigured ? await secondary.SearchAsync(keyword, maxResults, ct) : [];
        }

        var primaryResult = await primary.SearchAsync(keyword, maxResults, ct);
        if (primaryResult.Count > 0 || !secondary.IsConfigured)
        {
            return primaryResult;
        }

        // Primário não trouxe nada. Por quê? (o primário reporta o erro no status, se houve.)
        var primaryError = status.LastError;
        var secondaryResult = await secondary.SearchAsync(keyword, maxResults, ct);

        // Se o primário FALHOU (cota/limite) e a reserva salvou, deixa claro no painel que estamos
        // na reserva — o secundário, ao ter sucesso, limpou o status; reescrevemos a mensagem.
        if (primaryError is not null && secondaryResult.Count > 0)
        {
            status.SetLastError($"{primaryError} Usando SearXNG como reserva.");
        }
        return secondaryResult;
    }
}
