using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do "sair do grupo" no hop que é CÓDIGO NOSSO: WahaClient.LeaveGroupAsync → requisição HTTP
/// pra WAHA. O servidor WAHA é simulado por um HttpMessageHandler falso (não precisa de banco nem
/// de WhatsApp real). Cobre o contrato que define a corretude do fluxo:
///   - monta o JID completo (&lt;numero&gt;@g.us) e o método/rota corretos;
///   - 2xx/204 = sucesso; 404 = sucesso (não é mais membro);
///   - 422/409/5xx LANÇAM (não toleramos, pra não dar falso "saiu" deixando o usuário no grupo).
/// O único hop que isto NÃO cobre é a saída real no servidor do WhatsApp (precisa de conta real).
/// </summary>
public sealed class LeaveGroupE2ETests
{
    // Handler falso: captura a última requisição e responde com o status configurado.
    private sealed class StubHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    private static (WahaClient Client, StubHandler Handler) Build(HttpStatusCode status)
    {
        var handler = new StubHandler(status);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://waha.test/") };
        return (new WahaClient(http, Options.Create(new WahaOptions())), handler);
    }

    [Fact]
    public async Task Sucesso_faz_POST_no_jid_completo_e_rota_correta()
    {
        var (client, handler) = Build(HttpStatusCode.OK);

        await client.LeaveGroupAsync("default", "919569086931-1499323433", CancellationToken.None);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        // Desescapa pra comparar sem depender de %40 vs @ na normalização do Uri.
        var url = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.AbsoluteUri);
        url.Should().Be("http://waha.test/api/default/groups/919569086931-1499323433@g.us/leave");
    }

    [Fact]
    public async Task Status_204_nao_lanca()
    {
        var (client, _) = Build(HttpStatusCode.NoContent);

        var act = async () => await client.LeaveGroupAsync("default", "123", CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Status_404_e_tratado_como_sucesso_pois_ja_nao_e_membro()
    {
        var (client, _) = Build(HttpStatusCode.NotFound);

        var act = async () => await client.LeaveGroupAsync("default", "123", CancellationToken.None);

        await act.Should().NotThrowAsync("404 = número já não é membro, objetivo atingido");
    }

    [Theory]
    [InlineData(HttpStatusCode.UnprocessableEntity)] // 422
    [InlineData(HttpStatusCode.Conflict)]            // 409
    [InlineData(HttpStatusCode.InternalServerError)] // 500
    [InlineData(HttpStatusCode.BadGateway)]          // 502
    public async Task Erro_real_lanca_para_nao_dar_falso_saiu(HttpStatusCode status)
    {
        var (client, _) = Build(status);

        var act = async () => await client.LeaveGroupAsync("default", "123", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>(
            "tolerar esses códigos diria 'saiu' sem ter saído, deixando o usuário preso no grupo");
    }

    [Fact]
    public async Task Jid_ja_completo_nao_ganha_sufixo_duplicado()
    {
        var (client, handler) = Build(HttpStatusCode.OK);

        await client.LeaveGroupAsync("default", "120363410949918818@g.us", CancellationToken.None);

        var url = Uri.UnescapeDataString(handler.LastRequest!.RequestUri!.AbsoluteUri);
        url.Should().Contain("/groups/120363410949918818@g.us/leave");
        url.Should().NotContain("@g.us@g.us");
    }
}
