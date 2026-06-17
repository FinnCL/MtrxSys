using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>
/// Busca grupos por nicho com SearXNG (buscador open-source auto-hospedado, web inteira, sem chave).
/// Em duas etapas, porque o código do convite raramente aparece no TRECHO do resultado — ele vive
/// no CORPO da página de listagem: (1) pergunta ao SearXNG quais páginas falam do nicho; (2) visita
/// essas páginas (em paralelo) e extrai os <c>chat.whatsapp.com/&lt;código&gt;</c> do HTML. Sem URL
/// configurada, se reporta não-configurada e o Coletor cai no Telegram.
/// </summary>
internal sealed class SearxngSearchSource(
    HttpClient http,
    IOptions<CollectorOptions> opts,
    ILogger<SearxngSearchSource> logger) : IGroupLinkSearchSource
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const int MaxSearchPages = 3;  // páginas de resultados do SearXNG pra juntar URLs.
    private const int MaxPagesToScan = 12; // teto de páginas externas cujo corpo vamos varrer.
    private const string Source = "searxng";

    public bool IsConfigured => !string.IsNullOrWhiteSpace(opts.Value.SearxngBaseUrl);

    public async Task<IReadOnlyList<RawGroupLink>> SearchAsync(string keyword, int maxResults, CancellationToken ct)
    {
        keyword = keyword?.Trim() ?? string.Empty;
        if (!IsConfigured || keyword.Length == 0)
        {
            return [];
        }

        var baseUrl = opts.Value.SearxngBaseUrl!.TrimEnd('/');
        var want = Math.Clamp(maxResults, 1, 200);
        // Query mira páginas de listagem BR (rende mais que só o domínio do convite). Configurável.
        var template = string.IsNullOrWhiteSpace(opts.Value.SearchQueryTemplate)
            ? "grupos de whatsapp {keyword}"
            : opts.Value.SearchQueryTemplate;
        var q = Uri.EscapeDataString(template.Replace("{keyword}", keyword, StringComparison.OrdinalIgnoreCase));
        // Allowlist de diretórios (vazia = aceita qualquer página dos resultados).
        var dirs = opts.Value.DirectorySites ?? [];
        var byCode = new Dictionary<string, RawGroupLink>(StringComparer.Ordinal);

        // 1) Pergunta ao SearXNG quais páginas falam do nicho (junta as URLs). De passagem, captura
        //    códigos que por acaso já estejam no trecho (raro, mas sai de graça).
        var pageUrls = new List<string>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var page = 1; page <= MaxSearchPages && pageUrls.Count < MaxPagesToScan; page++)
        {
            ct.ThrowIfCancellationRequested();
            var results = await QuerySearxAsync(baseUrl, q, page, keyword, ct);
            if (results is null || results.Count == 0)
            {
                break;
            }
            var addedUrls = 0;
            foreach (var item in results)
            {
                AddCodes(byCode, WhatsAppInviteParser.ExtractCodes($"{item.Url} {item.Title} {item.Content}"));
                if (IsScannable(item.Url) && IsAllowedHost(item.Url, dirs) && seenUrls.Add(item.Url!) && pageUrls.Count < MaxPagesToScan)
                {
                    pageUrls.Add(item.Url!);
                    addedUrls++;
                }
            }
            if (addedUrls == 0)
            {
                break; // página de busca sem URL nova → não vale paginar mais.
            }
        }

        // 2) Onde os links REALMENTE estão: o corpo das páginas. Visita em paralelo (limitado) e
        //    extrai os convites do HTML — uma página de listagem rende vários de uma vez.
        var harvested = await Task.WhenAll(pageUrls.Select(url => FetchCodesAsync(url, ct)));
        foreach (var codes in harvested)
        {
            AddCodes(byCode, codes);
        }

        return byCode.Values.Take(want).ToList();
    }

    private static void AddCodes(Dictionary<string, RawGroupLink> byCode, IReadOnlyList<string> codes)
    {
        foreach (var code in codes)
        {
            byCode.TryAdd(code, new RawGroupLink(code, $"https://chat.whatsapp.com/{code}", Source));
        }
    }

    private async Task<List<SearxResult>?> QuerySearxAsync(
        string baseUrl, string q, int page, string keyword, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync($"{baseUrl}/search?q={q}&format=json&pageno={page}&language=pt-BR", ct);
            if (!resp.IsSuccessStatusCode)
            {
                // 403 = limiter/bloqueio; 5xx = instabilidade. Loga e devolve null (não derruba a coleta).
                logger.LogWarning("SearXNG retornou {Status} para o nicho '{Keyword}'.", (int)resp.StatusCode, keyword);
                return null;
            }
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
            logger.LogWarning(ex, "Falha ao consultar o SearXNG para o nicho '{Keyword}'.", keyword);
            return null;
        }
#pragma warning restore CA1031
    }

    private async Task<IReadOnlyList<string>> FetchCodesAsync(string url, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                return [];
            }
            var html = await resp.Content.ReadAsStringAsync(ct);
            return WhatsAppInviteParser.ExtractCodes(html);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch
        {
            return []; // página morta/lenta/binária/JS: ignora e segue nas outras.
        }
#pragma warning restore CA1031
    }

    // Vale visitar o corpo? Só http(s) PÚBLICO; pula links do WhatsApp (o código já vem na URL) e
    // hosts internos/privados — anti-SSRF: a fonte baixa URLs vindas da busca, que poderiam apontar
    // pra serviços internos (postgres, api, etc.) se não filtrássemos.
    private static bool IsScannable(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Contains("whatsapp.com", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }
        return (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) && IsPublicHost(u.Host);
    }

    // Bloqueia loopback, IPs privados (RFC1918), link-local e nomes de host de uma label só
    // (ex.: "api", "postgres", "searxng" — serviços internos do compose). Só host público passa.
    private static bool IsPublicHost(string host)
    {
        if (string.IsNullOrEmpty(host) || host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        if (System.Net.IPAddress.TryParse(host, out var ip))
        {
            if (System.Net.IPAddress.IsLoopback(ip))
            {
                return false;
            }
            var b = ip.GetAddressBytes();
            if (b.Length == 4)
            {
                if (b[0] is 10 or 0 or 127) return false;
                if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
                if (b[0] == 192 && b[1] == 168) return false;
                if (b[0] == 169 && b[1] == 254) return false;
            }
            return true;
        }
        return host.Contains('.'); // nome com domínio; label única = serviço interno.
    }

    // Allowlist de diretórios: vazia = qualquer host (público) passa; preenchida = só esses domínios.
    // Casa o domínio exato ou subdomínio (.dominio) — evita "notgruposwhats.app" casar com "gruposwhats.app".
    private static bool IsAllowedHost(string? url, string[] dirs)
    {
        if (dirs.Length == 0)
        {
            return true;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            return false;
        }
        return dirs.Any(d =>
            u.Host.Equals(d, StringComparison.OrdinalIgnoreCase)
            || u.Host.EndsWith("." + d, StringComparison.OrdinalIgnoreCase));
    }

    private sealed record SearxResponse(List<SearxResult>? Results);
    private sealed record SearxResult(string? Url, string? Title, string? Content);
}
