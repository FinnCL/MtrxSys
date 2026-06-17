using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>Item cru de um resultado de busca (de qualquer motor): a URL da página, o título e o
/// trecho. O código do convite quase nunca está no trecho — vive no CORPO da página de listagem.</summary>
internal sealed record SearchResultItem(string? Url, string? Title, string? Content);

/// <summary>
/// Lógica COMUM aos motores de busca (SearXNG, Serper): dada a lista de resultados, extrai os
/// convites do trecho (de brinde) e, principalmente, do CORPO das páginas — visitando-as em
/// paralelo, com anti-SSRF, allowlist de diretórios e teto. Fonte ÚNICA: evita duplicar isto por motor.
/// </summary>
internal static class InvitePageHarvester
{
    private const int MaxPagesToScan = 12; // teto de páginas externas cujo corpo vamos varrer.

    public static async Task<IReadOnlyList<RawGroupLink>> HarvestAsync(
        HttpClient http, string source, IReadOnlyList<SearchResultItem> results,
        string[] dirs, int maxResults, CancellationToken ct)
    {
        var byCode = new Dictionary<string, RawGroupLink>(StringComparer.Ordinal);
        var pageUrls = new List<string>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in results)
        {
            // De passagem, captura códigos que por acaso já estejam no trecho (raro, mas sai de graça).
            AddCodes(byCode, source, WhatsAppInviteParser.ExtractCodes($"{item.Url} {item.Title} {item.Content}"));
            if (IsScannable(item.Url) && IsAllowedHost(item.Url, dirs) && seenUrls.Add(item.Url!) && pageUrls.Count < MaxPagesToScan)
            {
                pageUrls.Add(item.Url!);
            }
        }

        // Onde os links REALMENTE estão: o corpo das páginas. Visita em paralelo (limitado pelo teto)
        // e extrai os convites do HTML — uma página de listagem rende vários de uma vez.
        var harvested = await Task.WhenAll(pageUrls.Select(url => FetchCodesAsync(http, url, ct)));
        foreach (var codes in harvested)
        {
            AddCodes(byCode, source, codes);
        }
        return byCode.Values.Take(Math.Clamp(maxResults, 1, 200)).ToList();
    }

    private static void AddCodes(Dictionary<string, RawGroupLink> byCode, string source, IReadOnlyList<string> codes)
    {
        foreach (var code in codes)
        {
            byCode.TryAdd(code, new RawGroupLink(code, $"https://chat.whatsapp.com/{code}", source));
        }
    }

    private static async Task<IReadOnlyList<string>> FetchCodesAsync(HttpClient http, string url, CancellationToken ct)
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
}
