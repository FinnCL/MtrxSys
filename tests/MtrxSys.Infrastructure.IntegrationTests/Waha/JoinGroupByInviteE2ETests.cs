using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do "entrar no grupo por convite" no hop que é CÓDIGO NOSSO: WahaClient.JoinGroupByInviteAsync.
/// Servidor WAHA simulado por HttpMessageHandler falso. O ponto crítico do fluxo (o que evita
/// "entrou mas importa 0 em silêncio") é o ID DEVOLVIDO: precisa ser o JID do grupo, NUNCA o código
/// do convite. Quando o join não traz um JID utilizável, resolvemos pelo /groups casando o nome.
/// </summary>
public sealed class JoinGroupByInviteE2ETests
{
    // Roteia por método+caminho: POST /groups/join e GET /groups respondem JSONs distintos.
    private sealed class RouteHandler(string joinJson, string groupsJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            var json = path.EndsWith("/groups/join", StringComparison.Ordinal) ? joinJson
                : path.EndsWith("/groups", StringComparison.Ordinal) ? groupsJson
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static WahaClient Build(string joinJson, string groupsJson = "{}")
    {
        var http = new HttpClient(new RouteHandler(joinJson, groupsJson)) { BaseAddress = new Uri("http://waha.test/") };
        return new WahaClient(http, Options.Create(new WahaOptions()));
    }

    [Fact]
    public async Task Join_com_jid_valido_usa_o_jid_do_grupo()
    {
        var client = Build("""{ "id": "120363111@g.us", "name": "Grupo X" }""");

        var g = await client.JoinGroupByInviteAsync("default", "AbCdEfGhIjKl", CancellationToken.None);

        g.Id.Should().Be("120363111@g.us");
        g.Name.Should().Be("Grupo X");
    }

    [Fact]
    public async Task Join_sem_id_resolve_o_jid_pelo_nome_no_groups()
    {
        // join não devolve id (resposta mínima); /groups (NOWEB: objeto por JID) tem o grupo pelo nome.
        var client = Build(
            joinJson: """{ "name": "Grupo Y" }""",
            groupsJson: """{ "120363222@g.us": { "id": "120363222@g.us", "subject": "Grupo Y", "participants": [] } }""");

        var g = await client.JoinGroupByInviteAsync("default", "AbCdEfGhIjKl", CancellationToken.None);

        // Id = número do grupo (sem @g.us, como o app faz circular) — o import depois adiciona o sufixo.
        g.Id.Should().Be("120363222");
        g.Name.Should().Be("Grupo Y");
    }

    [Fact]
    public async Task Join_que_devolve_o_codigo_do_convite_no_id_NAO_o_usa_como_grupo()
    {
        // Regressão do bug: id == código do convite (base62, com letras) NÃO é um JID. Sem grupo
        // casável no /groups, o resultado é id vazio (e o front desabilita o import) — jamais o código.
        var client = Build(joinJson: """{ "id": "AbCdEfGhIjKlMnOp", "name": "Grupo W" }""", groupsJson: "{}");

        var g = await client.JoinGroupByInviteAsync("default", "AbCdEfGhIjKlMnOp", CancellationToken.None);

        g.Id.Should().BeEmpty("o código do convite nunca pode virar id de grupo (importaria 0 em silêncio)");
        g.Name.Should().Be("Grupo W");
    }

    [Fact]
    public async Task Join_sem_id_e_nome_ambiguo_nao_arrisca_id_errado()
    {
        // Dois grupos com o mesmo nome → não dá pra desambiguar → id vazio (melhor que id de outro grupo).
        var client = Build(
            joinJson: """{ "name": "Promo" }""",
            groupsJson: """
            {
              "120363001@g.us": { "id": "120363001@g.us", "subject": "Promo", "participants": [] },
              "120363002@g.us": { "id": "120363002@g.us", "subject": "Promo", "participants": [] }
            }
            """);

        var g = await client.JoinGroupByInviteAsync("default", "AbCdEfGhIjKl", CancellationToken.None);

        g.Id.Should().BeEmpty();
    }
}
