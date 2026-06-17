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
/// Contrato da fonte SearXNG. O handler falso roteia por URL: o /search (JSON) lista PÁGINAS, e a
/// fonte então VISITA o corpo dessas páginas pra extrair os convites (onde os links realmente
/// ficam). Cobre: extração do corpo, dedup, teto, snippet de brinde, não-configurado, e erro.
/// </summary>
public sealed class SearxngSearchSourceTests
{
    private const string CodeA = "AAAAAAAAAAAAAAAAAAAA"; // 20 chars base62 (regex aceita 12-40)
    private const string CodeB = "BBBBBBBBBBBBBBBBBBBB";
    private const string CodeC = "CCCCCCCCCCCCCCCCCCCC";

    private sealed class RouteHandler(Func<Uri, HttpResponseMessage> route) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Seen.Add(request.RequestUri!);
            return Task.FromResult(route(request.RequestUri!));
        }
    }

    private static HttpResponseMessage Ok(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/html") };

    private static SearxngSearchSource Build(RouteHandler handler, CollectorOptions? opts = null)
    {
        var http = new HttpClient(handler);
        opts ??= new CollectorOptions { SearxngBaseUrl = "http://searxng.test" };
        return new SearxngSearchSource(
            http, Options.Create(opts), new InMemorySearchUsageMeter(),
            new InMemorySearchStatus(), NullLogger<SearxngSearchSource>.Instance);
    }

    // SearXNG lista 2 páginas; uma tem convites no corpo, a outra está vazia.
    private static HttpResponseMessage Route(Uri uri, string listBodyHtml)
    {
        var s = uri.ToString();
        if (s.Contains("searxng.test/search"))
        {
            return Ok("""
            { "results": [
              { "url": "http://pages.test/lista", "title": "Grupos do nicho", "content": "varios grupos" },
              { "url": "http://pages.test/vazia", "title": "Sem nada", "content": "nada aqui" }
            ] }
            """);
        }
        if (s.Contains("pages.test/lista"))
        {
            return Ok(listBodyHtml);
        }
        return new HttpResponseMessage(HttpStatusCode.NotFound); // pages.test/vazia
    }

    [Fact]
    public void IsConfigured_reflete_a_url()
    {
        Build(new RouteHandler(_ => Ok("{}"))).IsConfigured.Should().BeTrue();
        Build(new RouteHandler(_ => Ok("{}")), new CollectorOptions()).IsConfigured.Should().BeFalse();
    }

    [Fact]
    public async Task Nao_configurado_retorna_vazio_sem_chamar()
    {
        var handler = new RouteHandler(_ => Ok("{}"));
        var source = Build(handler, new CollectorOptions()); // sem URL

        var result = await source.SearchAsync("bet", 30, CancellationToken.None);

        result.Should().BeEmpty();
        handler.Seen.Should().BeEmpty();
    }

    [Fact]
    public async Task Extrai_os_convites_do_CORPO_das_paginas_listadas()
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
        result.Should().OnlyContain(r => r.InviteUrl == $"https://chat.whatsapp.com/{r.InviteCode}");
        result.Should().OnlyContain(r => r.SourceChannel == "searxng");
        // Visitou o SearXNG e as 2 páginas listadas.
        handler.Seen.Should().Contain(u => u.ToString().Contains("pages.test/lista"));
    }

    [Fact]
    public async Task Combina_codigo_do_snippet_com_os_do_corpo()
    {
        // CodeC vem no snippet do resultado de busca; CodeA/CodeB vêm do corpo da página.
        var handler = new RouteHandler(u =>
        {
            var s = u.ToString();
            if (s.Contains("searxng.test/search"))
            {
                return Ok($$"""
                { "results": [
                  { "url": "http://pages.test/lista", "title": "Grupos", "content": "veja chat.whatsapp.com/{{CodeC}}" }
                ] }
                """);
            }
            return Ok($"""<a href="https://chat.whatsapp.com/{CodeA}">a</a> chat.whatsapp.com/{CodeB}""");
        });
        var source = Build(handler);

        var result = await source.SearchAsync("bet", 30, CancellationToken.None);

        result.Select(r => r.InviteCode).Should().BeEquivalentTo([CodeA, CodeB, CodeC]);
    }

    [Fact]
    public async Task Deduplica_o_mesmo_codigo()
    {
        var listHtml = $"chat.whatsapp.com/{CodeA} e de novo chat.whatsapp.com/{CodeA}";
        var source = Build(new RouteHandler(u => Route(u, listHtml)));

        var result = await source.SearchAsync("bet", 30, CancellationToken.None);

        result.Should().ContainSingle().Which.InviteCode.Should().Be(CodeA);
    }

    [Fact]
    public async Task Respeita_o_teto_maxResults()
    {
        var listHtml = $"chat.whatsapp.com/{CodeA} chat.whatsapp.com/{CodeB} chat.whatsapp.com/{CodeC}";
        var source = Build(new RouteHandler(u => Route(u, listHtml)));

        var result = await source.SearchAsync("bet", 2, CancellationToken.None);

        result.Should().HaveCount(2, "o teto era 2");
    }

    [Fact]
    public async Task SearXNG_com_erro_retorna_vazio_sem_lancar()
    {
        var handler = new RouteHandler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden));
        var source = Build(handler);

        var act = async () => await source.SearchAsync("bet", 30, CancellationToken.None);

        (await act.Should().NotThrowAsync()).Subject.Should().BeEmpty();
    }

    [Fact]
    public async Task DirectorySites_so_varre_dominios_permitidos()
    {
        var handler = new RouteHandler(u =>
        {
            var s = u.ToString();
            if (s.Contains("searxng.test/search"))
            {
                return Ok("""
                { "results": [
                  { "url": "http://gruposwhats.app/lista", "title": "ok", "content": "" },
                  { "url": "http://spam-estrangeiro.com/lista", "title": "x", "content": "" }
                ] }
                """);
            }
            if (s.Contains("gruposwhats.app"))
            {
                return Ok($"chat.whatsapp.com/{CodeA}");
            }
            if (s.Contains("spam-estrangeiro.com"))
            {
                return Ok($"chat.whatsapp.com/{CodeB}");
            }
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var source = Build(handler, new CollectorOptions
        {
            SearxngBaseUrl = "http://searxng.test",
            DirectorySites = ["gruposwhats.app"],
        });

        var result = await source.SearchAsync("bet", 30, CancellationToken.None);

        result.Select(r => r.InviteCode).Should().BeEquivalentTo([CodeA], "só o diretório permitido é varrido");
        handler.Seen.Should().NotContain(u => u.ToString().Contains("spam-estrangeiro.com"));
    }
}
