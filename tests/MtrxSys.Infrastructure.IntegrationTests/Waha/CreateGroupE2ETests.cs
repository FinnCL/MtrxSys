using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do "criar grupo" no hop que é CÓDIGO NOSSO: WahaClient.CreateGroupAsync → requisição HTTP pra
/// WAHA. Servidor simulado por HttpMessageHandler falso. Cobre o contrato que define a corretude:
///   - sessão vai como SEGMENTO (/api/{session}/groups), NÃO em query param como nos contatos;
///   - participantes viram {id: "&lt;dígitos&gt;@c.us"};
///   - o id devolvido é normalizado pra MESMA forma do ListGroups (sem @g.us) — sem isso o grupo
///     nunca casa com a listagem e o destaque "é meu" some sem erro nenhum;
///   - falha HTTP e resposta sem id LANÇAM (criar é ação: engolir viraria grupo órfão).
/// O único hop que isto NÃO cobre é a criação real no servidor do WhatsApp (precisa de conta real).
/// </summary>
public sealed class CreateGroupE2ETests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(status) { Content = new StringContent(body) };
        }
    }

    private static (WahaClient Client, StubHandler Handler) Build(
        HttpStatusCode status = HttpStatusCode.Created,
        string body = """{"id":"120363410949918818@g.us"}""")
    {
        var handler = new StubHandler(status, body);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://waha.test/") };
        return (new WahaClient(http, Options.Create(new WahaOptions())), handler);
    }

    [Fact]
    public async Task Envia_POST_com_a_sessao_no_caminho_e_participantes_como_chat_id()
    {
        var (client, handler) = Build();

        await client.CreateGroupAsync(
            "default", "Amigos", ["+5571999998888", "+5511888887777"], CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        var url = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.AbsoluteUri);
        url.Should().Be("http://waha.test/api/default/groups",
            "a sessão vai no caminho; uniformizar com a query param dos contatos dá 404");

        using var sent = JsonDocument.Parse(handler.LastBody!);
        sent.RootElement.GetProperty("name").GetString().Should().Be("Amigos");
        sent.RootElement.GetProperty("participants")
            .EnumerateArray()
            .Select(p => p.GetProperty("id").GetString())
            .Should().Equal("5571999998888@c.us", "5511888887777@c.us");
    }

    [Fact]
    public async Task Id_devolvido_perde_o_sufixo_para_casar_com_a_listagem()
    {
        var (client, _) = Build(body: """{"id":"120363410949918818@g.us"}""");

        var group = await client.CreateGroupAsync("default", "Amigos", ["+5571999998888"], CancellationToken.None);

        group.Id.Should().Be("120363410949918818",
            "o ListGroups devolve o número antes do '@'; guardar o JID faria o grupo nunca ser reconhecido como meu");
    }

    [Fact]
    public async Task Id_em_objeto_serialized_tambem_e_lido()
    {
        var (client, _) = Build(body: """{"id":{"_serialized":"120363410949918818@g.us","user":"120363410949918818"}}""");

        var group = await client.CreateGroupAsync("default", "Amigos", ["+5571999998888"], CancellationToken.None);

        group.Id.Should().Be("120363410949918818");
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)] // 422 — sessão não-WORKING
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task Falha_http_lanca_em_vez_de_degradar(HttpStatusCode status)
    {
        var (client, _) = Build(status, "{}");

        var act = async () => await client.CreateGroupAsync("default", "Amigos", ["+5571999998888"], CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>(
            "criar é ação: degradar pra vazio diria 'criado' sem ter criado");
    }

    [Fact]
    public async Task Resposta_sem_id_lanca_em_vez_de_devolver_grupo_orfao()
    {
        var (client, _) = Build(body: """{"name":"Amigos"}""");

        var act = async () => await client.CreateGroupAsync("default", "Amigos", ["+5571999998888"], CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "sem id não dá pra registrar como meu nem importar membros, e o grupo já existe no WhatsApp");
    }
}
