using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Alerting;

// POSTa { "text": message } no Alert:WebhookUrl (compatível com Slack/Discord e relays genéricos —
// pra Telegram/email, aponte pra um relay que aceite {text}). Sem URL configurada, é no-op (quem chama
// já loga). Best-effort: qualquer falha aqui é só logada, nunca sobe pro chamador.
internal sealed class WebhookAlertNotifier(
    HttpClient http,
    IOptions<AlertOptions> opts,
    ILogger<WebhookAlertNotifier> log) : IAlertNotifier
{
    public async Task NotifyAsync(string message, CancellationToken ct)
    {
        var url = opts.Value.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }
        try
        {
            using var resp = await http.PostAsJsonAsync(url, new { text = message }, ct);
            if (!resp.IsSuccessStatusCode)
            {
                log.LogWarning("Alerta: webhook respondeu {Status}", (int)resp.StatusCode);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
#pragma warning disable CA1031 // alerta best-effort: falha de rede/URL não pode derrubar o watch
        catch (Exception ex)
        {
            log.LogWarning(ex, "Alerta: falha ao POSTar no webhook (o alerta segue no log).");
        }
#pragma warning restore CA1031
    }
}
