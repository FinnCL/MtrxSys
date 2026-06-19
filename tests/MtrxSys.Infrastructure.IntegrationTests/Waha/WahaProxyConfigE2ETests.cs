using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do proxy por chip no hop que é CÓDIGO NOSSO: WahaClient injeta o proxy no CONFIG DA SESSÃO
/// (config.proxy) via API do WAHA. Isto trava a correção da falha comprovada: o WAHA 2026.x
/// (CORE/NOWEB) IGNORA a env var WHATSAPP_PROXY_SERVER — então o proxy SÓ funciona via session config.
/// Se alguém reverter pro mecanismo da env var, estes testes quebram.
/// </summary>
public sealed class WahaProxyConfigE2ETests
{
    private sealed record Call(string Method, string Path, string Body);

    private sealed class RecordingHandler(Func<HttpRequestMessage, (HttpStatusCode, string)> responder) : HttpMessageHandler
    {
        public List<Call> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(ct);
            Calls.Add(new Call(request.Method.Method, request.RequestUri!.AbsolutePath, body));
            var (code, json) = responder(request);
            return new HttpResponseMessage(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }

    private static WahaOptions ProxyOpts() => new()
    {
        ProxyServer = "br.decodo.com:10001",
        ProxyUsername = "user1",
        ProxyPassword = "pass1",
    };

    private static (WahaClient Client, RecordingHandler Handler) Build(
        WahaOptions opts, Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
    {
        var handler = new RecordingHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://waha.test/") };
        return (new WahaClient(http, Options.Create(opts)), handler);
    }

    [Fact]
    public async Task CriarSessao_injeta_o_proxy_no_config_desde_o_start()
    {
        // start devolve 404 (sessão não existe) -> força a criação; criação responde 201.
        var (client, handler) = Build(ProxyOpts(), req =>
            req.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)
                ? (HttpStatusCode.NotFound, "{}")
                : (HttpStatusCode.Created, "{}"));

        await client.EnsureSessionStartedAsync("default", CancellationToken.None);

        var create = handler.Calls.Single(c => c.Method == "POST" && c.Path == "/api/sessions");
        // O proxy precisa estar JÁ na criação, senão a 1ª conexão vazaria pelo IP da máquina.
        create.Body.Should().Contain("\"proxy\"");
        create.Body.Should().Contain("br.decodo.com:10001");
        create.Body.Should().Contain("\"username\":\"user1\"");
        create.Body.Should().Contain("\"start\":true");
    }

    [Fact]
    public async Task Proxy_com_credencial_pela_metade_manda_so_o_server_sem_auth()
    {
        // Só user, sem pass: NÃO manda "username"/"password" (evita "password":null malformado).
        // Vira server-only -> proxy sem auth falha de forma visível (chip não conecta), fail-safe.
        var opts = new WahaOptions { ProxyServer = "br.decodo.com:10001", ProxyUsername = "user1" };
        var (client, handler) = Build(opts, req =>
            req.RequestUri!.AbsolutePath.EndsWith("/start", StringComparison.Ordinal)
                ? (HttpStatusCode.NotFound, "{}")
                : (HttpStatusCode.Created, "{}"));

        await client.EnsureSessionStartedAsync("default", CancellationToken.None);

        var create = handler.Calls.Single(c => c.Method == "POST" && c.Path == "/api/sessions");
        create.Body.Should().Contain("\"proxy\"");
        create.Body.Should().Contain("br.decodo.com:10001");
        create.Body.Should().NotContain("username");
        create.Body.Should().NotContain("password");
    }

    [Fact]
    public async Task EnsureWebhook_grava_proxy_e_religa_quando_o_proxy_muda()
    {
        // Sessão existente SEM proxy e SEM o webhook -> espera PUT com proxy+webhook e um restart.
        var (client, handler) = Build(ProxyOpts(), req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return (HttpStatusCode.OK, """{ "name":"default","status":"WORKING","config":{ "webhooks":[] } }""");
            }
            return (HttpStatusCode.OK, "{}");
        });

        var ok = await client.EnsureWebhookConfiguredAsync(
            "default", "http://api:8080/webhooks/waha", ["message", "message.any"], CancellationToken.None);

        ok.Should().BeTrue();
        var put = handler.Calls.Single(c => c.Method == "PUT" && c.Path == "/api/sessions/default");
        put.Body.Should().Contain("\"proxy\"");
        put.Body.Should().Contain("br.decodo.com:10001");
        put.Body.Should().Contain("http://api:8080/webhooks/waha");
        // Proxy passou de "nenhum" para "setado" -> religa a sessão pra valer na reconexão (sem QR).
        handler.Calls.Should().ContainSingle(c => c.Method == "POST" && c.Path == "/api/sessions/default/restart");
    }

    [Fact]
    public async Task EnsureWebhook_sem_proxy_configurado_nao_manda_proxy_nem_religa()
    {
        // Sem ProxyServer: comportamento antigo preservado — PUT só com webhook, nenhum restart.
        var (client, handler) = Build(new WahaOptions(), req =>
            req.Method == HttpMethod.Get
                ? (HttpStatusCode.OK, """{ "name":"default","status":"WORKING","config":{ "webhooks":[] } }""")
                : (HttpStatusCode.OK, "{}"));

        await client.EnsureWebhookConfiguredAsync(
            "default", "http://api:8080/webhooks/waha", ["message"], CancellationToken.None);

        var put = handler.Calls.Single(c => c.Method == "PUT" && c.Path == "/api/sessions/default");
        put.Body.Should().NotContain("\"proxy\"");
        handler.Calls.Should().NotContain(c => c.Path.EndsWith("/restart", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnsureWebhook_quando_proxy_ja_bate_nao_religa()
    {
        // Proxy já gravado igual ao desejado e webhook já presente -> nada a fazer (sem PUT, sem restart).
        var (client, handler) = Build(ProxyOpts(), req => (HttpStatusCode.OK, """
            {
              "name":"default","status":"WORKING",
              "config":{
                "webhooks":[ { "url":"http://api:8080/webhooks/waha" } ],
                "proxy":{ "server":"br.decodo.com:10001" }
              }
            }
            """));

        var ok = await client.EnsureWebhookConfiguredAsync(
            "default", "http://api:8080/webhooks/waha", ["message"], CancellationToken.None);

        ok.Should().BeTrue();
        handler.Calls.Should().NotContain(c => c.Method == "PUT");
        handler.Calls.Should().NotContain(c => c.Path.EndsWith("/restart", StringComparison.Ordinal));
    }
}
