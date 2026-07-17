using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do endereçamento por LID no hop que é CÓDIGO NOSSO: antes de enviar, o WahaClient resolve o LID
/// do número (GET /lids/pn) e manda pro "{lid}@lid" em vez de "{num}@c.us". Isso corrige o erro 463
/// ("missing tctoken"), comprovado em produção: envio por @c.us dá ack=-1 (não entrega), o aparelho
/// manda por @lid e entrega. Sem LID / flag desligado → cai no @c.us (comportamento de hoje).
/// </summary>
public sealed class WahaLidAddressingE2ETests
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

    private static (WahaClient Client, RecordingHandler Handler) Build(
        WahaOptions opts, Func<HttpRequestMessage, (HttpStatusCode, string)> responder)
    {
        var handler = new RecordingHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://waha.test/") };
        return (new WahaClient(http, Options.Create(opts)), handler);
    }

    // Responder padrão: /lids/pn devolve o LID; sendText devolve um id.
    private static (HttpStatusCode, string) Respond(HttpRequestMessage req, bool hasLid)
    {
        var path = req.RequestUri!.AbsolutePath;
        if (path.Contains("/lids/pn/", StringComparison.Ordinal))
        {
            return hasLid
                ? (HttpStatusCode.OK, """{ "lid":"95907039207522@lid", "pn":"557185211291@c.us" }""")
                : (HttpStatusCode.NotFound, "{}");
        }
        return (HttpStatusCode.Created, """{ "id":"wamid.TEST" }""");
    }

    [Fact]
    public async Task Com_LID_envia_pro_lid_e_nao_pro_cus()
    {
        var (client, handler) = Build(new WahaOptions(), req => Respond(req, hasLid: true));

        await client.SendTextAsync("default", "557185211291@c.us", "oi", CancellationToken.None);

        handler.Calls.Should().Contain(c => c.Method == "GET" && c.Path == "/api/default/lids/pn/557185211291");
        var send = handler.Calls.Single(c => c.Method == "POST" && c.Path == "/api/sendText");
        send.Body.Should().Contain("\"chatId\":\"95907039207522@lid\"", "envia pelo LID (evita 463)");
        send.Body.Should().NotContain("557185211291@c.us");
    }

    [Fact]
    public async Task Sem_LID_cai_no_cus()
    {
        var (client, handler) = Build(new WahaOptions(), req => Respond(req, hasLid: false));

        await client.SendTextAsync("default", "557185211291@c.us", "oi", CancellationToken.None);

        var send = handler.Calls.Single(c => c.Method == "POST" && c.Path == "/api/sendText");
        send.Body.Should().Contain("\"chatId\":\"557185211291@c.us\"", "sem LID mantém o comportamento de hoje");
    }

    [Fact]
    public async Task Flag_desligado_nao_resolve_LID()
    {
        var (client, handler) = Build(new WahaOptions { PreferLidAddressing = false }, req => Respond(req, hasLid: true));

        await client.SendTextAsync("default", "557185211291@c.us", "oi", CancellationToken.None);

        handler.Calls.Should().NotContain(c => c.Path.Contains("/lids/pn/", StringComparison.Ordinal));
        var send = handler.Calls.Single(c => c.Method == "POST" && c.Path == "/api/sendText");
        send.Body.Should().Contain("\"chatId\":\"557185211291@c.us\"");
    }

    [Fact]
    public async Task Erro_na_resolucao_do_LID_cai_no_cus_sem_derrubar_envio()
    {
        // Simula timeout/connection reset no /lids/pn: o responder LANÇA. A resolução é best-effort —
        // NUNCA pode virar exceção no SendText; tem que cair no @c.us e o envio seguir.
        var (client, handler) = Build(new WahaOptions(), req =>
        {
            if (req.RequestUri!.AbsolutePath.Contains("/lids/pn/", StringComparison.Ordinal))
            {
                throw new HttpRequestException("boom");
            }
            return (HttpStatusCode.Created, """{ "id":"wamid.TEST" }""");
        });

        var act = async () => await client.SendTextAsync("default", "557185211291@c.us", "oi", CancellationToken.None);
        await act.Should().NotThrowAsync("um blip no /lids/pn não pode derrubar o envio");

        var send = handler.Calls.Single(c => c.Method == "POST" && c.Path == "/api/sendText");
        send.Body.Should().Contain("\"chatId\":\"557185211291@c.us\"", "caiu no @c.us");
    }

    [Fact]
    public async Task Grupo_nao_resolve_LID()
    {
        var (client, handler) = Build(new WahaOptions(), req => Respond(req, hasLid: true));

        await client.SendTextAsync("default", "120363427071225036@g.us", "oi", CancellationToken.None);

        handler.Calls.Should().NotContain(c => c.Path.Contains("/lids/pn/", StringComparison.Ordinal));
        var send = handler.Calls.Single(c => c.Method == "POST" && c.Path == "/api/sendText");
        send.Body.Should().Contain("\"chatId\":\"120363427071225036@g.us\"");
    }
}
