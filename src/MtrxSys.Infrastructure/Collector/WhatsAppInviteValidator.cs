using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MtrxSys.Core.Application.Abstractions;

namespace MtrxSys.Infrastructure.Collector;

/// <summary>
/// Valida convites do WhatsApp pela página pública de convite. O HttpClient já tem BaseAddress
/// https://chat.whatsapp.com/, então buscamos só o código. Convite VIVO traz o nome do grupo no
/// meta og:title; MORTO/revogado vem com og:title vazio.
/// </summary>
internal sealed class WhatsAppInviteValidator(
    HttpClient http,
    ILogger<WhatsAppInviteValidator> logger) : IWhatsAppInviteValidator
{
    // WhatsApp emite <meta property="og:title" content="<nome do grupo>"> (property antes de content).
    private static readonly Regex OgTitleRx =
        new("<meta[^>]*\\bproperty=\"og:title\"[^>]*\\bcontent=\"([^\"]*)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<InviteCheck?> CheckAsync(string inviteCode, CancellationToken ct)
    {
        try
        {
            using var resp = await http.GetAsync(Uri.EscapeDataString(inviteCode), ct);
            if (!resp.IsSuccessStatusCode)
            {
                // Bloqueio/limite/erro transitório → null (não decide; re-tenta depois).
                return null;
            }
            var html = await resp.Content.ReadAsStringAsync(ct);
            var raw = OgTitleRx.Match(html) is { Success: true } m ? m.Groups[1].Value : string.Empty;
            // og:title vem com entidades HTML (&amp;, &#39;, &quot;…); decodifica pro nome real do grupo.
            var name = System.Net.WebUtility.HtmlDecode(raw).Trim();
            // og:title preenchido = grupo vivo (com nome); vazio = revogado/expirado/inválido.
            return string.IsNullOrEmpty(name) ? new InviteCheck(false, null) : new InviteCheck(true, name);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao validar o convite {Code} pela página pública.", inviteCode);
            return null;
        }
#pragma warning restore CA1031
    }
}
