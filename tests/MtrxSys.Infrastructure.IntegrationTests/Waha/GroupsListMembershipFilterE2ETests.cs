using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do FILTRO DE PARTICIPAÇÃO da lista de grupos no engine NOWEB (o da produção). Regressão do bug:
/// a aba Grupos mostrava o CACHE de metadados do NOWEB — grupos que o número SAIU ou que sumiram do
/// aparelho voltavam a cada Atualizar (inclusive DUPLICADOS: JID velho stale + o atual). O NOWEB pulava
/// o filtro que o WEBJS já tinha, porque a decisão de membro usava um DTO sem `phoneNumber` (onde o
/// NOWEB põe o número real). Agora o filtro usa PhoneFromParticipant (funciona nos dois engines).
/// Servidor WAHA simulado por HttpMessageHandler.
/// </summary>
public sealed class GroupsListMembershipFilterE2ETests
{
    private const string OwnDigits = "5511999999999";
    private const string OwnMeJson = """{ "me": { "id": "5511999999999@c.us" } }""";

    // Roteia: POST /groups/join, GET /sessions/{id} (número próprio), GET /groups (lista NOWEB),
    // GET .../{jid}/participants.
    private sealed class RouteHandler(
        string groupsJson, Dictionary<string, string> participantsByGroupNumber, string joinJson) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.EndsWith("/groups/join", StringComparison.Ordinal))
            {
                json = joinJson;
            }
            else if (path.EndsWith("/participants", StringComparison.Ordinal))
            {
                var match = participantsByGroupNumber.FirstOrDefault(kv => path.Contains(kv.Key, StringComparison.Ordinal));
                json = match.Value ?? "[]";
            }
            else if (path.EndsWith("/groups", StringComparison.Ordinal))
            {
                json = groupsJson;
            }
            else if (path.Contains("/sessions/", StringComparison.Ordinal))
            {
                json = OwnMeJson;
            }
            else
            {
                json = "{}";
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static WahaClient Build(string groupsJson, Dictionary<string, string> participants, string joinJson = "{}") =>
        new(
            new HttpClient(new RouteHandler(groupsJson, participants, joinJson)) { BaseAddress = new Uri("http://waha.test/") },
            Options.Create(new WahaOptions()));

    [Fact]
    public async Task Esconde_grupo_do_qual_o_numero_saiu()
    {
        var groups = """
        {
          "120363001@g.us": { "id": "120363001@g.us", "subject": "Grupo Ativo", "participants": [{}] },
          "120363002@g.us": { "id": "120363002@g.us", "subject": "Grupo Sai", "participants": [{}] }
        }
        """;
        var participants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["120363001"] = $$"""[{ "phoneNumber": "{{OwnDigits}}@c.us" }, { "phoneNumber": "5511888887777@c.us" }]""",
            ["120363002"] = "[]", // saí → o WAHA devolve lista vazia (perdi a visibilidade dos membros)
        };

        var result = await Build(groups, participants).ListGroupsAsync("default", CancellationToken.None);

        result.Select(g => g.Id).Should().BeEquivalentTo(["120363001"]);
    }

    [Fact]
    public async Task Remove_o_fantasma_duplicado_mantendo_so_o_ativo()
    {
        // O caso da tela: dois JIDs com o MESMO nome (um ativo, um fantasma do cache do NOWEB).
        var groups = """
        {
          "120363010@g.us": { "id": "120363010@g.us", "subject": "PALPITES LUCIEL", "participants": [{}] },
          "120363011@g.us": { "id": "120363011@g.us", "subject": "PALPITES LUCIEL", "participants": [{}] }
        }
        """;
        var participants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["120363010"] = $$"""[{ "phoneNumber": "{{OwnDigits}}@c.us" }]""", // ativo (estou nele)
            ["120363011"] = "[]", // fantasma
        };

        var result = await Build(groups, participants).ListGroupsAsync("default", CancellationToken.None);

        result.Select(g => g.Id).Should().BeEquivalentTo(["120363010"]);
    }

    [Fact]
    public async Task Join_resolve_grupo_recem_entrado_mesmo_com_participants_vazio()
    {
        // REGRESSÃO: o join (sem id) resolve o JID casando o NOME no /groups. Se essa resolução usasse a
        // lista FILTRADA, um grupo recém-entrado cujo /participants ainda não sincronizou (vazio) seria
        // escondido → id vazio → import 0 EM SILÊNCIO. A resolução tem que usar a lista CRUA.
        var groups = """{ "120363099@g.us": { "id": "120363099@g.us", "subject": "Grupo Novo", "participants": [{}] } }""";
        var participants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["120363099"] = "[]", // recém-entrado: WAHA ainda não sincronizou os membros
        };
        var client = Build(groups, participants, joinJson: """{ "name": "Grupo Novo" }""");

        var g = await client.JoinGroupByInviteAsync("default", "AbCdEfGhIjKl", CancellationToken.None);

        g.Id.Should().Be("120363099", "o grupo recém-entrado é resolvido pela lista CRUA, sem o filtro esconder");
    }

    [Fact]
    public async Task Preserva_grupo_quando_participantes_sao_so_lid()
    {
        // Ambiguidade real: lista populada só com @lid (nenhum número decifrável) → não dá pra decidir
        // → preserva (melhor exibir um a mais do que esconder um real).
        var groups = """{ "120363020@g.us": { "id": "120363020@g.us", "subject": "So LID", "participants": [{}] } }""";
        var participants = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["120363020"] = """[{ "id": "111111111@lid" }, { "id": "222222222@lid" }]""",
        };

        var result = await Build(groups, participants).ListGroupsAsync("default", CancellationToken.None);

        result.Select(g => g.Id).Should().BeEquivalentTo(["120363020"]);
    }
}
