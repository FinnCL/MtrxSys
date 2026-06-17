using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Collector;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// Contrato da fonte Serper. O handler falso roteia por host: o POST pro google.serper.dev devolve
/// os resultados orgânicos (PÁGINAS), e a fonte então VISITA o corpo dessas páginas pra extrair os
/// convites (onde os links ficam). Cobre: extração do corpo, não-configurado, e — crítico de
/// segurança — a chave X-API-KEY viaja SÓ pro Serper, nunca pras páginas de terceiros.
/// </summary>
public sealed class SerperSearchSourceTests
{
    private const string CodeA = "AAAAAAAAAAAAAAAAAAAA";
    private const string CodeB = "BBBBBBBBBBBBBBBBBBBB";

    private sealed class RouteHandler(Func<Uri, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<(Uri Url, bool HasKey)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add((request.RequestUri!, request.Headers.Contains("X-API-KEY")));
            return Task.FromResult(route(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(string body, string mime) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mime) };

    private static SerperSearchSource Build(
        RouteHandler handler, CollectorOptions? opts = null, ISearchUsageMeter? meter = null, ISearchStatus? status = null)
    {
        var http = new HttpClient(handler);
        opts ??= new CollectorOptions { SerperApiKey = "test-key" };
        return new SerperSearchSource(
            http, Options.Create(opts), meter ?? new InMemorySearchUsageMeter(),
            status ?? new InMemorySearchStatus(), NullLogger<SerperSearchSource>.Instance);
    }

    // Serper devolve 1 página orgânica; a página tem os convites no corpo.
    private static HttpResponseMessage Route(Uri uri, string listBodyHtml)
    {
        if (uri.Host.Contains("serper.dev", StringComparison.OrdinalIgnoreCase))
        {
            return Ok("""
            { "organic": [
              { "title": "Grupos do nicho", "link": "http://pages.test/lista", "snippet": "varios grupos" }
            ] }
            """, "application/json");
        }
        if (uri.ToString().Contains("pages.test/lista"))
        {
            return Ok(listBodyHtml, "text/html");
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }

    [Fact]
    public void IsConfigured_reflete_a_chave()
    {
        Build(new RouteHandler(_ => Ok("{}", "application/json"))).IsConfigured.Should().BeTrue();
        Build(new RouteHandler(_ => Ok("{}", "application/json")), new CollectorOptions()).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Nao_configurado_retorna_vazio_sem_chamar()
    {
        var handler = new RouteHandler(_ => Ok("{}", "application/json"));
        var source = Build(handler, new CollectorOptions()); // sem chave

        var result = await source.SearchAsync("bet", 30, CancellationToken.None);

        result.Should().BeEmpty();
        handler.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Extrai_os_convites_do_CORPO_das_paginas_organicas()
    {
        var listHtml = $"""
        <html><body>
          <a href="https://chat.whatsapp.com/{CodeA}">Grupo A</a>
          <p>entre tambem em chat.whatsapp.com/{CodeB} agora</p>
        </body></html>
        """;
        var handler = new RouteHandler(u => Route(u, listHtml));
        var source = Build(handler);

        var result = await source.SearchAsync("bet", 30, CancellationToken.None);

        result.Select(r => r.InviteCode).Should().BeEquivalentTo([CodeA, CodeB]);
        result.Should().OnlyContain(r => r.SourceChannel == "serper");
    }

    [Fact]
    public async Task A_chave_viaja_so_pro_Serper_e_nao_vaza_pras_paginas()
    {
        var handler = new RouteHandler(u => Route(u, $"chat.whatsapp.com/{CodeA}"));
        var source = Build(handler);

        await source.SearchAsync("bet", 30, CancellationToken.None);

        // A chamada ao Serper leva a chave; a visita à página de terceiros NÃO.
        handler.Seen.Should().Contain(x => x.Url.Host.Contains("serper.dev") && x.HasKey);
        handler.Seen.Should().Contain(x => x.Url.ToString().Contains("pages.test") && !x.HasKey);
        handler.Seen.Should().NotContain(x => x.Url.ToString().Contains("pages.test") && x.HasKey);
    }

    [Fact]
    public async Task Conta_as_requisicoes_feitas_no_medidor()
    {
        var meter = new InMemorySearchUsageMeter();
        var handler = new RouteHandler(u => Route(u, $"chat.whatsapp.com/{CodeA}"));
        var source = Build(handler, meter: meter);

        await source.SearchAsync("bet", 30, CancellationToken.None);

        (await meter.GetCountAsync(CancellationToken.None)).Should().BeGreaterThan(0, "cada chamada ao Serper conta pro painel");
    }

    [Fact]
    public async Task Em_429_reporta_o_motivo_no_status()
    {
        var status = new InMemorySearchStatus();
        var meter = new InMemorySearchUsageMeter();
        // Serper responde 429 (limite); a página nem é alcançada.
        var handler = new RouteHandler(u => u.Host.Contains("serper.dev", StringComparison.OrdinalIgnoreCase)
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        var source = Build(handler, meter: meter, status: status);

        var result = await source.SearchAsync("bet", 30, CancellationToken.None);

        result.Should().BeEmpty();
        status.LastError.Should().NotBeNull().And.Contain("429", "o painel precisa mostrar POR QUE a busca parou");
        (await meter.GetCountAsync(CancellationToken.None)).Should().Be(0, "429 não é cobrado → não conta crédito");
    }

    [Fact]
    public async Task Serper_com_erro_retorna_vazio_sem_lancar()
    {
        var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var source = Build(handler);

        var act = async () => await source.SearchAsync("bet", 30, CancellationToken.None);

        (await act.Should().NotThrowAsync()).Subject.Should().BeEmpty();
    }
}
