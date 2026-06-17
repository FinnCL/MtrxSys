using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using MtrxSys.Infrastructure.Collector;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// Contrato do validador de convite pela página pública, com HttpMessageHandler falso. Convite vivo
/// tem og:title com o nome do grupo; morto/revogado tem og:title vazio; erro HTTP → null (re-tenta).
/// </summary>
public sealed class WhatsAppInviteValidatorTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/html"),
            });
    }

    private static WhatsAppInviteValidator Build(HttpStatusCode status, string body)
    {
        var http = new HttpClient(new StubHandler(status, body)) { BaseAddress = new Uri("https://chat.whatsapp.com/") };
        return new WhatsAppInviteValidator(http, NullLogger<WhatsAppInviteValidator>.Instance);
    }

    [Fact]
    public async Task Convite_vivo_tem_og_title_com_nome()
    {
        var c = Build(HttpStatusCode.OK,
            """<html><head><meta property="og:title" content="Grupo Marketing BR"><meta property="og:description" content="Convite para grupo do WhatsApp"></head></html>""");

        var r = await c.CheckAsync("ABC123", CancellationToken.None);

        r.Should().NotBeNull();
        r!.Alive.Should().BeTrue();
        r.Name.Should().Be("Grupo Marketing BR");
    }

    [Fact]
    public async Task Convite_morto_tem_og_title_vazio()
    {
        var c = Build(HttpStatusCode.OK,
            """<html><head><meta property="og:title" content=""><meta property="og:description" content="Convite para grupo do WhatsApp"></head></html>""");

        var r = await c.CheckAsync("ABC123", CancellationToken.None);

        r.Should().NotBeNull();
        r!.Alive.Should().BeFalse();
        r.Name.Should().BeNull();
    }

    [Fact]
    public async Task Sem_og_title_conta_como_morto()
    {
        var c = Build(HttpStatusCode.OK, "<html><head></head></html>");

        (await c.CheckAsync("ABC123", CancellationToken.None))!.Alive.Should().BeFalse();
    }

    [Fact]
    public async Task Erro_http_retorna_null_para_re_tentar()
    {
        var c = Build(HttpStatusCode.TooManyRequests, "");

        (await c.CheckAsync("ABC123", CancellationToken.None)).Should().BeNull();
    }
}
