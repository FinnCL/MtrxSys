using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>
/// Busca grupos por nicho via Serper (google.serper.dev) — resultados do índice do Google por API,
/// com faixa grátis (~2.500 buscas). Mesma estratégia em duas etapas do SearXNG: o Serper devolve as
/// PÁGINAS relevantes e o <see cref="InvitePageHarvester"/> visita o corpo delas pra extrair os
/// convites. A chave (Collector:SerperApiKey) só viaja PRO Serper — nunca pras páginas visitadas.
/// Vazia = não-configurada (o Coletor cai no SearXNG/Telegram).
/// </summary>
internal sealed class SerperSearchSource(
    HttpClient http,
    IOptions<CollectorOptions> opts,
    ISearchUsageMeter meter,
    ISearchStatus status,
    ILogger<SerperSearchSource> logger) : IGroupLinkSearchSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxSearchPages = 3;
    private const string Source = "serper";

    public string Engine => "Serper";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(opts.Value.SerperApiKey);

    public async Task<IReadOnlyList<RawGroupLink>> SearchAsync(string keyword, int maxResults, CancellationToken ct)
    {
        keyword = keyword?.Trim() ?? string.Empty;
        if (!IsConfigured || keyword.Length == 0)
        {
            return [];
        }

        var template = string.IsNullOrWhiteSpace(opts.Value.SearchQueryTemplate)
            ? "grupos de whatsapp {keyword}"
            : opts.Value.SearchQueryTemplate;
        var query = template.Replace("{keyword}", keyword, StringComparison.OrdinalIgnoreCase);

        var results = new List<SearchResultItem>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxSearchPages; page++)
        {
            ct.ThrowIfCancellationRequested();
            var organic = await QuerySerperAsync(query, page, keyword, ct);
            if (organic is null || organic.Count == 0)
            {
                break;
            }
            var added = 0;
            foreach (var o in organic)
            {
                results.Add(new SearchResultItem(o.Link, o.Title, o.Snippet));
                if (!string.IsNullOrEmpty(o.Link) && seenUrls.Add(o.Link))
                {
                    added++;
                }
            }
            if (added == 0)
            {
                break; // página sem URL nova → não vale paginar mais (e economiza créditos).
            }
        }

        return await InvitePageHarvester.HarvestAsync(http, Source, results, opts.Value.DirectorySites ?? [], maxResults, ct);
    }

    private async Task<List<SerperOrganic>?> QuerySerperAsync(string query, int page, string keyword, CancellationToken ct)
    {
        try
        {
            var baseUrl = (string.IsNullOrWhiteSpace(opts.Value.SerperBaseUrl)
                ? "https://google.serper.dev"
                : opts.Value.SerperBaseUrl).TrimEnd('/');
            // X-API-KEY SÓ aqui (na chamada ao Serper) — não como header default, pra a chave não
            // vazar pras páginas de terceiros que o harvester visita depois com o mesmo HttpClient.
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl}/search");
            req.Headers.Add("X-API-KEY", opts.Value.SerperApiKey);
            req.Content = JsonContent.Create(new { q = query, gl = "br", hl = "pt-br", num = 20, page }, options: Json);
            using var resp = await http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 401/403 = chave inválida/sem crédito; 429 = limite. Reporta o motivo pro painel,
                // loga e devolve null (não derruba a coleta). NÃO conta no medidor: 4xx/429 não são
                // cobrados pelo Serper — contar aqui superestimaria o consumo.
                status.SetLastError(DescribeError((int)resp.StatusCode));
                logger.LogWarning("Serper retornou {Status} para o nicho '{Keyword}'.", (int)resp.StatusCode, keyword);
                return null;
            }
            // Conta só requisição BEM-SUCEDIDA (2xx) — é o que o Serper cobra (≈ 1 crédito).
            await meter.RecordAsync(ct);
            status.SetLastError(null); // sucesso → limpa o último erro.
            var body = await resp.Content.ReadFromJsonAsync<SerperResponse>(Json, ct);
            return body?.Organic;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            status.SetLastError("Serper: falha de conexão.");
            logger.LogWarning(ex, "Falha ao consultar o Serper para o nicho '{Keyword}'.", keyword);
            return null;
        }
#pragma warning restore CA1031
    }

    // Mensagem amigável pro painel a partir do status HTTP do Serper.
    private static string DescribeError(int status) => status switch
    {
        401 or 403 => $"Serper recusou (HTTP {status}): chave inválida ou sem crédito.",
        429 => "Serper recusou (HTTP 429): limite de requisições atingido.",
        _ => $"Serper retornou HTTP {status}.",
    };

    private sealed record SerperResponse(List<SerperOrganic>? Organic);
    private sealed record SerperOrganic(string? Title, string? Link, string? Snippet);
}
