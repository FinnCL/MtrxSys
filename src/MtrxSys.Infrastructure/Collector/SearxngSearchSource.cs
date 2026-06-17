using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>
/// Busca grupos por nicho com SearXNG (buscador open-source auto-hospedado, web inteira, sem chave).
/// Pergunta ao SearXNG quais PÁGINAS falam do nicho; o <see cref="InvitePageHarvester"/> visita o
/// corpo dessas páginas e extrai os <c>chat.whatsapp.com/&lt;código&gt;</c> (onde os links realmente
/// ficam). Sem URL configurada, se reporta não-configurada e o Coletor cai no Telegram.
/// </summary>
internal sealed class SearxngSearchSource(
    HttpClient http,
    IOptions<CollectorOptions> opts,
    ISearchUsageMeter meter,
    ISearchStatus status,
    ILogger<SearxngSearchSource> logger) : IGroupLinkSearchSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxSearchPages = 3; // páginas de resultados do SearXNG pra juntar URLs.
    private const string Source = "searxng";

    public string Engine => "SearXNG";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(opts.Value.SearxngBaseUrl);

    public async Task<IReadOnlyList<RawGroupLink>> SearchAsync(string keyword, int maxResults, CancellationToken ct)
    {
        keyword = keyword?.Trim() ?? string.Empty;
        if (!IsConfigured || keyword.Length == 0)
        {
            return [];
        }

        var baseUrl = opts.Value.SearxngBaseUrl!.TrimEnd('/');
        // Query mira páginas de listagem BR (rende mais que só o domínio do convite). Configurável.
        var template = string.IsNullOrWhiteSpace(opts.Value.SearchQueryTemplate)
            ? "grupos de whatsapp {keyword}"
            : opts.Value.SearchQueryTemplate;
        var q = Uri.EscapeDataString(template.Replace("{keyword}", keyword, StringComparison.OrdinalIgnoreCase));

        // Pergunta ao SearXNG quais páginas falam do nicho, juntando os resultados (dedup por URL).
        var results = new List<SearchResultItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxSearchPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var hits = await QuerySearxAsync(baseUrl, q, page, keyword, ct);
            if (hits is null || hits.Count == 0)
            {
                break;
            }
            var added = 0;
            foreach (var h in hits)
            {
                results.Add(new SearchResultItem(h.Url, h.Title, h.Content));
                if (!string.IsNullOrEmpty(h.Url) && seenUrls.Add(h.Url))
                {
                    added++;
                }
            }
            if (added == 0)
            {
                break; // página de busca sem URL nova → não vale paginar mais.
            }
        }

        return await InvitePageHarvester.HarvestAsync(http, Source, results, opts.Value.DirectorySites ?? [], maxResults, ct);
    }

    private async Task<List<SearxResult>?> QuerySearxAsync(
        string baseUrl, string q, int page, string keyword, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/search?q={q}&format=json&pageno={page}&language=pt-BR", ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 403 = limiter/bloqueio; 5xx = instabilidade. Reporta pro painel, loga, devolve null.
                status.SetLastError($"SearXNG retornou HTTP {(int)resp.StatusCode}.");
                logger.LogWarning("SearXNG retornou {Status} para o nicho '{Keyword}'.", (int)resp.StatusCode, keyword);
                return null;
            }
            await meter.RecordAsync(ct); // conta só requisição bem-sucedida (consistência com o Serper).
            status.SetLastError(null); // sucesso → limpa o último erro.
            var body = await resp.Content.ReadFromJsonAsync<SearxResponse>(Json, ct);
            return body?.Results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            status.SetLastError("SearXNG: falha de conexão.");
            logger.LogWarning(ex, "Falha ao consultar o SearXNG para o nicho '{Keyword}'.", keyword);
            return null;
        }
#pragma warning restore CA1031
    }

    private sealed record SearxResponse(List<SearxResult>? Results);
    private sealed record SearxResult(string? Url, string? Title, string? Content);
}
