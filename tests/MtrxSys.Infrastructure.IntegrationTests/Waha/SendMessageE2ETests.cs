using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do ENVIO no hop que é CÓDIGO NOSSO: WahaClient.SendTextAsync/SendImageAsync — o caminho mais
/// crítico do produto (toda mensagem do disparo passa aqui). Servidor WAHA simulado por
/// HttpMessageHandler. Garante o contrato que o DispatchEngine depende:
///   - o chatId vai DERIVADO do telefone (+55… → 55…@c.us), com text + session no corpo;
///   - o id da mensagem é extraído das 3 formas que as engines devolvem (string / {_serialized} / {id});
///   - resposta sem id vira string vazia (NÃO lança — a mensagem já saiu; lançar geraria reenvio dup);
///   - status de erro PROPAGA (o disparo marca falha e re-tenta, em vez de achar que enviou).
/// </summary>
public sealed class SendMessageE2ETests
{
    private sealed class SendStub : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = """{"id":"true_5511_ABC"}""";
        public string? LastPath { get; private set; }
        public string LastBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/json") };
        }
    }

    private static (WahaClient Client, SendStub Stub) Build()
    {
        var stub = new SendStub();
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://waha.test/") };
        return (new WahaClient(http, Options.Create(new WahaOptions())), stub);
    }

    [Fact]
    public async Task SendText_deriva_chatId_do_telefone_e_manda_text_e_session()
    {
        var (client, stub) = Build();

        await client.SendTextAsync("default", "+5511999998888", "ola mundo", CancellationToken.None);

        stub.LastPath.Should().Be("/api/sendText");
        stub.LastBody.Should().Contain("\"chatId\":\"5511999998888@c.us\"", "o + vira dígitos + sufixo @c.us");
        stub.LastBody.Should().Contain("\"text\":\"ola mundo\"");
        stub.LastBody.Should().Contain("\"session\":\"default\"");
    }

    [Fact]
    public async Task SendText_preserva_chatId_de_grupo_sem_remontar()
    {
        var (client, stub) = Build();

        await client.SendTextAsync("default", "120363041234567890@g.us", "oi grupo", CancellationToken.None);

        stub.LastBody.Should().Contain("\"chatId\":\"120363041234567890@g.us\"", "já tem @ → não remonta");
    }

    [Theory]
    [InlineData("""{"id":"true_5511_ABC"}""", "true_5511_ABC")]                 // string
    [InlineData("""{"id":{"_serialized":"SER_123"}}""", "SER_123")]            // objeto _serialized
    [InlineData("""{"id":{"id":"INNER_9"}}""", "INNER_9")]                     // objeto id aninhado
    public async Task SendText_extrai_o_id_das_tres_formas(string body, string expectedId)
    {
        var (client, stub) = Build();
        stub.Body = body;

        var id = await client.SendTextAsync("default", "+5511999998888", "x", CancellationToken.None);

        id.Should().Be(expectedId);
    }

    [Fact]
    public async Task SendText_sem_id_devolve_vazio_e_nao_lanca()
    {
        var (client, stub) = Build();
        stub.Body = """{"status":"ok"}"""; // sem campo id

        var id = await client.SendTextAsync("default", "+5511999998888", "x", CancellationToken.None);

        id.Should().BeEmpty("a mensagem já saiu — lançar aqui marcaria falha e geraria reenvio duplicado");
    }

    [Fact]
    public async Task SendText_status_de_erro_propaga_para_o_disparo_re_tentar()
    {
        var (client, stub) = Build();
        stub.Status = HttpStatusCode.InternalServerError;
        stub.Body = """{"error":"boom"}""";

        var act = async () => await client.SendTextAsync("default", "+5511999998888", "x", CancellationToken.None);

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SendImage_manda_base64_mimetype_e_filename_certos()
    {
        var (client, stub) = Build();
        var bytes = new byte[] { 1, 2, 3, 4 };

        await client.SendImageAsync("default", "+5511999998888", bytes, "image/png", "legenda", CancellationToken.None);

        stub.LastPath.Should().Be("/api/sendImage");
        stub.LastBody.Should().Contain("\"chatId\":\"5511999998888@c.us\"");
        stub.LastBody.Should().Contain("\"caption\":\"legenda\"");
        stub.LastBody.Should().Contain($"\"data\":\"{Convert.ToBase64String(bytes)}\"");
        stub.LastBody.Should().Contain("\"mimetype\":\"image/png\"");
        stub.LastBody.Should().Contain("\"filename\":\"image.png\"", "a extensão vem do mimetype");
    }
}
