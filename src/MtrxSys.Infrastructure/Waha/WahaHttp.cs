using System.Text.Json;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;

namespace MtrxSys.Infrastructure.Waha;

/// <summary>Camada HTTP de baixo nível compartilhada pelos clientes WAHA por responsabilidade
/// (sessão/mensagens/grupos). Centraliza o que de fato repetia: montar a requisição com a API key,
/// o <see cref="JsonSerializerOptions"/>, o escaping de segmento de URL e o proxy de sessão. As
/// tolerâncias de status (404=ok, 422→degrada…) continuam em CADA método — são deliberadas e
/// documentadas, não duplicação a abstrair.</summary>
internal sealed class WahaHttp(HttpClient http, IOptions<WahaOptions> opts)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public WahaOptions Options => opts.Value;

    public HttpRequestMessage NewRequest(HttpMethod method, string relativeUri)
    {
        var req = new HttpRequestMessage(method, relativeUri);
        if (!string.IsNullOrWhiteSpace(opts.Value.ApiKey))
        {
            req.Headers.Add("X-Api-Key", opts.Value.ApiKey);
        }
        return req;
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct) =>
        http.SendAsync(req, ct);

    public static string Esc(string segment) => Uri.EscapeDataString(segment);

    // Objeto de proxy pro config de sessão do WAHA (null = sem proxy). Inclui credenciais só quando
    // houver — proxy sem auth manda só o server.
    public object? ProxyConfigOrNull()
    {
        var server = NormalizedProxyServer();
        if (server is null)
        {
            return null;
        }
        var user = opts.Value.ProxyUsername;
        var pass = opts.Value.ProxyPassword;
        // Só manda credenciais se as DUAS existirem. Meia-credencial (só user ou só pass) cairia
        // como "server sem auth" — que falha de forma VISÍVEL (chip não conecta) em vez de enviar
        // um "password":null malformado. O scripts/check-proxy-env.ps1 já alerta o .env meio-preenchido.
        if (!string.IsNullOrWhiteSpace(user) && !string.IsNullOrWhiteSpace(pass))
        {
            return new { server, username = user, password = pass };
        }
        return new { server };
    }

    // Config de webhook pro config de sessão (null = sem URL). Incluir JÁ na CRIAÇÃO é essencial: este
    // WAHA aplica a URL adicionada por PUT numa sessão rodando, mas NÃO o customHeader (token) — só na
    // criação/start. Sem isto, toda sessão recriada (reset/re-pareamento) mandava webhook SEM token →
    // 401 no app → opt-out/inbound/sensor quebrados até um restart.
    public object[]? WebhookConfigOrNull()
    {
        var url = opts.Value.WebhookCallbackUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }
        return WahaParsing.BuildWebhooks(url, opts.Value.WebhookEvents, opts.Value.WebhookToken);
    }

    public string? NormalizedProxyServer() => NormalizeServer(opts.Value.ProxyServer);

    // host:porta sem espaços e sem esquema (o WAHA quer "host:porta", não "http://host:porta").
    public static string? NormalizeServer(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        var s = raw.Trim();
        var scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0)
        {
            s = s[(scheme + 3)..];
        }
        return s.Length == 0 ? null : s;
    }
}
