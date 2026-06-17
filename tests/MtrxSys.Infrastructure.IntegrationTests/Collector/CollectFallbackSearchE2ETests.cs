using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Collector;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// E2E do fluxo de BUSCA com reserva, ponta a ponta com as fontes REAIS (SerperSearchSource +
/// SearxngSearchSource + InvitePageHarvester) atrás do CompositeSearchSource — só o servidor é
/// simulado por HttpMessageHandler. Prova o recurso novo:
///  - Serper esgotado (429) → cai no SearXNG SOZINHO, extrai os convites do corpo das páginas, avisa
///    no status, e conta só a requisição bem-sucedida (o 429 não conta crédito);
///  - Serper respondendo → usa o Serper e nem TOCA na reserva.
/// </summary>
public sealed class CollectFallbackSearchE2ETests
{
    private const string CodeA = "AAAAAAAAAAAAAAAAAAAA";
    private const string CodeB = "BBBBBBBBBBBBBBBBBBBB";

    // Servidor simulado: roteia por host. O Serper responde 200 (com 1 página) ou 429 conforme o
    // cenário; o SearXNG sempre responde com 1 página; a página tem os convites no corpo.
    private sealed class RouteHandler(bool serperOk) : HttpMessageHandler
    {
        public List<Uri> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var uri = request.RequestUri!;
            Seen.Add(uri);
            HttpResponseMessage resp;
            if (uri.Host.Contains("serper.dev", StringComparison.OrdinalIgnoreCase))
            {
                resp = serperOk
                    ? Json("""{ "organic": [ { "title": "Grupos", "link": "http://pages.test/lista", "snippet": "" } ] }""")
                    : new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            }
            else if (uri.Host.Contains("searxng.test", StringComparison.OrdinalIgnoreCase))
            {
                resp = Json("""{ "results": [ { "url": "http://pages.test/lista", "title": "Grupos", "content": "" } ] }""");
            }
            else if (uri.ToString().Contains("pages.test/lista", StringComparison.Ordinal))
            {
                resp = Html($"""<a href="https://chat.whatsapp.com/{CodeA}">a</a> chat.whatsapp.com/{CodeB}""");
            }
            else
            {
                resp = new HttpResponseMessage(HttpStatusCode.NotFound);
            }
            return Task.FromResult(resp);
        }

        private static HttpResponseMessage Json(string b) =>
            new(HttpStatusCode.OK) { Content = new StringContent(b, Encoding.UTF8, "application/json") };
        private static HttpResponseMessage Html(string b) =>
            new(HttpStatusCode.OK) { Content = new StringContent(b, Encoding.UTF8, "text/html") };
    }

    private static (CompositeSearchSource Composite, InMemorySearchUsageMeter Meter, InMemorySearchStatus Status, RouteHandler Handler)
        Build(bool serperOk)
    {
        var handler = new RouteHandler(serperOk);
        var opts = Options.Create(new CollectorOptions { SerperApiKey = "k", SearxngBaseUrl = "http://searxng.test" });
        var meter = new InMemorySearchUsageMeter();
        var status = new InMemorySearchStatus();
        var serper = new SerperSearchSource(new HttpClient(handler), opts, meter, status, NullLogger<SerperSearchSource>.Instance);
        var searx = new SearxngSearchSource(new HttpClient(handler), opts, meter, status, NullLogger<SearxngSearchSource>.Instance);
        return (new CompositeSearchSource(serper, searx, status), meter, status, handler);
    }

    [Fact]
    public async Task Serper_esgotado_cai_no_SearXNG_extrai_convites_e_avisa()
    {
        var (composite, meter, status, _) = Build(serperOk: false);

        var result = await composite.SearchAsync("bet", 30, CancellationToken.None);

        result.Select(r => r.InviteCode).Should().BeEquivalentTo([CodeA, CodeB], "a reserva (SearXNG) trouxe os grupos");
        result.Should().OnlyContain(r => r.SourceChannel == "searxng");
        status.LastError.Should().NotBeNull()
            .And.Contain("429", "o painel mostra que o Serper esgotou")
            .And.Contain("SearXNG", "e que está usando a reserva");
        (await meter.GetCountAsync(CancellationToken.None)).Should().BeGreaterThan(0, "as buscas BEM-SUCEDIDAS do SearXNG contam (o 429 não)");
    }

    [Fact]
    public async Task Serper_respondendo_usa_o_Serper_e_nao_toca_na_reserva()
    {
        var (composite, meter, status, handler) = Build(serperOk: true);

        var result = await composite.SearchAsync("bet", 30, CancellationToken.None);

        result.Select(r => r.InviteCode).Should().BeEquivalentTo([CodeA, CodeB]);
        result.Should().OnlyContain(r => r.SourceChannel == "serper");
        status.LastError.Should().BeNull("Serper funcionou — sem aviso");
        handler.Seen.Should().NotContain(u => u.Host.Contains("searxng.test", StringComparison.OrdinalIgnoreCase),
            "com Serper OK, a reserva nem é acionada");
        (await meter.GetCountAsync(CancellationToken.None)).Should().BeGreaterThan(0);
    }
}
