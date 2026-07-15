using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E da agenda do aparelho (WahaClient.ListContactsAsync) — o hop que é código nosso. Servidor
/// WAHA simulado por HttpMessageHandler falso. Prova o que a Fase Humana depende:
///   - a sessão vai como QUERY PARAM (`/api/contacts/all?session=`), não como segmento — a WAHA
///     destoa aqui do resto da API, e errar isso dá 404 silencioso (lista vazia);
///   - a marca isMyContact é PRESERVADA, não filtrada no cliente (quem filtra é o endpoint);
///   - grupo, @lid e o próprio número saem fora — viram contato-lixo;
///   - resposta de erro (422 sessão fora, 400 NOWEB sem store) degrada pra lista vazia.
/// </summary>
public sealed class ListContactsE2ETests
{
    private sealed class CapturingHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public Uri? LastUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (WahaClient Client, CapturingHandler Handler) Build(
        string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new CapturingHandler(json, status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://waha.test/") };
        return (new WahaClient(http, Options.Create(new WahaOptions())), handler);
    }

    [Fact]
    public async Task Sessao_vai_como_query_param_nao_como_segmento()
    {
        var (client, handler) = Build("[]");

        await client.ListContactsAsync("default", CancellationToken.None);

        handler.LastUri!.AbsolutePath.Should().Be("/api/contacts/all");
        handler.LastUri.Query.Should().Contain("session=default");
    }

    [Fact]
    public async Task Preserva_isMyContact_sem_filtrar()
    {
        // O cliente devolve o que a WAHA disse; quem decide é o endpoint. Se o filtro estivesse
        // aqui e o engine não preenchesse a marca, a lista viria vazia sem explicação.
        var (client, _) = Build("""
        [
          { "id": "5571900000001@c.us", "name": "João",  "isMyContact": true  },
          { "id": "5571900000002@c.us", "pushname": "Zé", "isMyContact": false }
        ]
        """);

        var contacts = await client.ListContactsAsync("default", CancellationToken.None);

        contacts.Should().HaveCount(2);
        contacts.Single(c => c.PhoneE164 == "+5571900000001").IsMyContact.Should().BeTrue();
        contacts.Single(c => c.PhoneE164 == "+5571900000002").IsMyContact.Should().BeFalse();
    }

    [Fact]
    public async Task Nome_prefere_o_da_agenda_e_cai_pro_pushname()
    {
        // `name` é o que o operador salvou no aparelho; `pushname` é o que a pessoa escolheu.
        var (client, _) = Build("""
        [
          { "id": "5571900000001@c.us", "name": "João da Obra", "pushname": "Jhon", "isMyContact": true },
          { "id": "5571900000002@c.us", "pushname": "Zé",                            "isMyContact": true }
        ]
        """);

        var contacts = await client.ListContactsAsync("default", CancellationToken.None);

        contacts.Single(c => c.PhoneE164 == "+5571900000001").Name.Should().Be("João da Obra");
        contacts.Single(c => c.PhoneE164 == "+5571900000002").Name.Should().Be("Zé");
    }

    [Fact]
    public async Task Descarta_grupo_lid_proprio_numero_e_pseudo_numero()
    {
        var (client, _) = Build("""
        [
          { "id": "5571900000001@c.us",       "name": "João",    "isMyContact": true },
          { "id": "120363111@g.us",           "name": "Grupo",   "isGroup": true     },
          { "id": "157239574847645@lid",      "name": "Oculto",  "isMyContact": true },
          { "id": "5571999999999@c.us",       "name": "Eu",      "isMe": true        },
          { "id": "0@c.us",                   "name": "Sistema", "isMyContact": true }
        ]
        """);

        var contacts = await client.ListContactsAsync("default", CancellationToken.None);

        contacts.Should().ContainSingle();
        contacts[0].PhoneE164.Should().Be("+5571900000001");
    }

    [Fact]
    public async Task Resposta_de_erro_degrada_para_lista_vazia()
    {
        // 422 = sessão fora do ar; 400 = NOWEB sem store. A aba mostra "nenhum contato" em vez de
        // 500 — que no browser ainda apareceria como erro de CORS.
        var (client, _) = Build("""{ "error": "session not working" }""", HttpStatusCode.UnprocessableEntity);

        (await client.ListContactsAsync("default", CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Aceita_objeto_indexado_por_jid()
    {
        // A NOWEB devolve os GRUPOS assim; barato tolerar a mesma forma nos contatos.
        var (client, _) = Build("""
        {
          "5571900000001@c.us": { "id": "5571900000001@c.us", "name": "João", "isMyContact": true }
        }
        """);

        var contacts = await client.ListContactsAsync("default", CancellationToken.None);

        contacts.Should().ContainSingle();
        contacts[0].Name.Should().Be("João");
    }
}
