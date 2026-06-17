using System.Text.RegularExpressions;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>
/// Lê canais públicos de Telegram via a prévia web (https://t.me/s/&lt;canal&gt;) — HTML puro, sem
/// login e sem JavaScript — e extrai os links de convite chat.whatsapp.com postados ali. Também
/// detecta menções a outros canais (descoberta automática), pra a base de fontes crescer sozinha.
/// </summary>
internal sealed class TelegramChannelSource(HttpClient http) : IGroupLinkSource
{
    // Handles de canal referenciados no HTML (t.me/<handle> ou t.me/s/<handle>).
    private static readonly Regex ChannelRx =
        new(@"t\.me/(?:s/)?([A-Za-z][A-Za-z0-9_]{3,31})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Caminhos do t.me que não são canais (evita poluir a descoberta).
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "s", "share", "joinchat", "addstickers", "addtheme", "proxy", "socks", "login", "iv", "bg",
    };

    public async Task<ChannelHarvest> FetchChannelAsync(string channel, CancellationToken ct)
    {
        var handle = channel.Trim().TrimStart('@');
        if (string.IsNullOrEmpty(handle))
        {
            return new ChannelHarvest([], []);
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, $"s/{Uri.EscapeDataString(handle)}");
        using var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
        {
            return new ChannelHarvest([], []);
        }
        var html = await resp.Content.ReadAsStringAsync(ct);

        var links = WhatsAppInviteParser.ExtractCodes(html)
            .Select(code => new RawGroupLink(code, $"https://chat.whatsapp.com/{code}", handle))
            .ToList();

        var referenced = new List<string>();
        var seenChannels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in ChannelRx.Matches(html))
        {
            var c = m.Groups[1].Value;
            if (Reserved.Contains(c) || c.Equals(handle, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (seenChannels.Add(c))
            {
                referenced.Add(c);
            }
        }

        return new ChannelHarvest(links, referenced);
    }
}
