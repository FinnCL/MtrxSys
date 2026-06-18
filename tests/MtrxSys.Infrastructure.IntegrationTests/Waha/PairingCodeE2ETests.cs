using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Infrastructure.Waha;

namespace MtrxSys.Infrastructure.IntegrationTests.Waha;

/// <summary>
/// E2E do "conectar por código" (alternativa ao QR), no hop que é CÓDIGO NOSSO. Servidor WAHA
/// simulado por HttpMessageHandler. Cobre o fluxo de ponta a ponta replicando o miolo do endpoint
/// POST /api/waha/pairing-code (valida status + dígitos e só então chama o WAHA), mais o contrato
/// do WahaClient.RequestPairingCodeAsync (forma da requisição, parsing e erros). Garante:
///   - só pede código quando a sessão está em scan, e nunca para número curto (não bate no WAHA à toa);
///   - o número vai SANITIZADO (só dígitos) pro WAHA;
///   - resposta sem "code" vira string vazia (a UI trata como "tente de novo", não caixa vazia);
///   - erro do WAHA PROPAGA em vez de inventar um código.
/// </summary>
public sealed class PairingCodeE2ETests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // Roteia: GET /api/sessions/{s} devolve o status configurado; POST .../auth/request-code captura
    // o corpo e responde o code (status/body configuráveis pra simular sucesso/erro).
    private sealed class WahaStub : HttpMessageHandler
    {
        public string Status { get; set; } = "SCAN_QR_CODE";
        public HttpStatusCode CodeHttp { get; set; } = HttpStatusCode.OK;
        public string CodeBody { get; set; } = """{"code":"TZDA-MMYH"}""";
        public int RequestCodeCalls { get; private set; }
        public string LastRequestCodeBody { get; private set; } = "";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/auth/request-code", StringComparison.Ordinal))
            {
                RequestCodeCalls++;
                LastRequestCodeBody = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
                return Json(CodeHttp, CodeBody);
            }
            if (path.StartsWith("/api/sessions/", StringComparison.Ordinal))
            {
                return Json(HttpStatusCode.OK, $"{{\"name\":\"default\",\"status\":\"{Status}\",\"me\":null}}");
            }
            return Json(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private static (WahaClient Client, WahaStub Stub) Build()
    {
        var stub = new WahaStub();
        var http = new HttpClient(stub) { BaseAddress = new Uri("http://waha.test/") };
        return (new WahaClient(http, Options.Create(new WahaOptions())), stub);
    }

    // Espelha o miolo de POST /api/waha/pairing-code (WahaEndpoints.cs): valida status + dígitos e só
    // então chama o WAHA. Retorna (status HTTP que o endpoint devolveria, código).
    private static async Task<(int Http, string? Code)> ViaEndpoint(WahaClient waha, string? phone)
    {
        var status = await waha.GetSessionStatusAsync("default", Ct);
        if (status != WahaSessionStatus.ScanQrCode)
        {
            return (409, null);
        }
        var digits = new string((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.Length < 12)
        {
            return (400, null);
        }
        var code = await waha.RequestPairingCodeAsync("default", digits, Ct);
        return (200, code);
    }

    [Fact]
    public async Task Em_scan_com_numero_valido_pede_e_devolve_o_codigo()
    {
        var (client, stub) = Build();

        var (http, code) = await ViaEndpoint(client, "5571993798077");

        http.Should().Be(200);
        code.Should().Be("TZDA-MMYH");
        stub.RequestCodeCalls.Should().Be(1);
        stub.LastRequestCodeBody.Should().Contain("5571993798077");
    }

    [Fact]
    public async Task Numero_com_simbolos_vai_so_em_digitos_pro_waha()
    {
        var (client, stub) = Build();

        var (http, _) = await ViaEndpoint(client, "+55 (71) 99379-8077");

        http.Should().Be(200);
        stub.LastRequestCodeBody.Should().Contain("5571993798077");
        stub.LastRequestCodeBody.Should().NotContain("+");
        stub.LastRequestCodeBody.Should().NotContain(" ");
    }

    [Fact]
    public async Task Sessao_fora_do_scan_devolve_409_e_nao_bate_no_waha()
    {
        var (client, stub) = Build();
        stub.Status = "WORKING"; // já pareado

        var (http, code) = await ViaEndpoint(client, "5571993798077");

        http.Should().Be(409);
        code.Should().BeNull();
        stub.RequestCodeCalls.Should().Be(0, "número já conectado não deve pedir código");
    }

    [Fact]
    public async Task Numero_curto_devolve_400_e_nao_bate_no_waha()
    {
        var (client, stub) = Build();

        var (http, _) = await ViaEndpoint(client, "5571"); // < 12 dígitos

        http.Should().Be(400);
        stub.RequestCodeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Resposta_sem_code_devolve_vazio()
    {
        var (client, stub) = Build();
        stub.CodeBody = """{"message":"ok"}"""; // WAHA pode omitir o code

        var code = await client.RequestPairingCodeAsync("default", "5571993798077", Ct);

        code.Should().BeEmpty("a UI trata vazio como 'tente de novo' em vez de mostrar caixa vazia");
    }

    [Fact]
    public async Task Erro_do_waha_propaga_em_vez_de_inventar_codigo()
    {
        var (client, stub) = Build();
        stub.CodeHttp = HttpStatusCode.BadRequest;
        stub.CodeBody = """{"error":"invalid phone number"}""";

        var act = async () => await client.RequestPairingCodeAsync("default", "5571993798077", Ct);

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
