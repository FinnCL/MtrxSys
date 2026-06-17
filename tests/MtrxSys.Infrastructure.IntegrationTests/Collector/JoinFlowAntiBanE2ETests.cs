using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Core.Safety;
using MtrxSys.Infrastructure.Waha;
using NSubstitute;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// E2E do caminho ANTI-BAN da entrada em grupo, replicando o miolo do endpoint POST /links/{code}/join
/// com JoinThrottle + WahaClient REAIS (servidor WAHA simulado). Prova o que protege o processo:
///   - uma entrada bem-sucedida REGISTRA e a próxima é BLOQUEADA dentro do intervalo (o espaçamento
///     que evita o ban); passado o intervalo, libera;
///   - um convite inválido (4xx) NÃO consome a trava — não queima a vaga do dia nem o intervalo.
/// (O fix de UI desta rodada — uma ação por vez — garante que as entradas chegam SERIALIZADAS aqui.)
/// </summary>
public sealed class JoinFlowAntiBanE2ETests
{
    private static readonly DateTimeOffset T0 = new(2026, 6, 17, 12, 0, 0, TimeSpan.Zero);

    private sealed class JoinStub(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int JoinCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/groups/join", StringComparison.Ordinal))
            {
                JoinCalls++;
            }
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static JoinThrottle Throttle(int interval = 120, int maxPerDay = 15)
    {
        var rng = Substitute.For<IRandomSource>();
        rng.NextInt(Arg.Any<int>(), Arg.Any<int>()).Returns(interval);
        return new JoinThrottle(rng, Options.Create(new CollectorOptions { MaxJoinsPerDay = maxPerDay }));
    }

    private static WahaClient Waha(JoinStub stub) =>
        new(new HttpClient(stub) { BaseAddress = new Uri("http://waha.test/") }, Options.Create(new WahaOptions()));

    // Link já validado (Resolved) — é o único estado em que o usuário pode clicar "Entrar".
    private static GroupLink Link(string code)
    {
        var l = GroupLink.Create(Guid.NewGuid(), code, $"https://chat.whatsapp.com/{code}", "serper", "bet", T0);
        l.Resolve("Grupo BR", null, T0);
        return l;
    }

    // Espelha o endpoint: Check (antes) → join → 4xx vira Invalid (sem registrar) → sucesso registra.
    private static async Task<string> TryJoinAsync(JoinThrottle throttle, WahaClient waha, GroupLink link, DateTimeOffset now)
    {
        if (!throttle.Check(now).Allowed)
        {
            return "blocked";
        }
        WahaGroup joined;
        try
        {
            joined = await waha.JoinGroupByInviteAsync("default", link.InviteCode, CancellationToken.None);
        }
        catch (HttpRequestException ex) when (ex.StatusCode is >= HttpStatusCode.BadRequest and < HttpStatusCode.InternalServerError)
        {
            link.MarkInvalid(now);
            return "invalid";
        }
        throttle.RegisterJoin(now);
        link.MarkJoined(joined.Id);
        return "joined";
    }

    [Fact]
    public async Task Entrada_registra_e_bloqueia_a_proxima_dentro_do_intervalo()
    {
        var stub = new JoinStub(HttpStatusCode.OK, """{ "id": "120363111@g.us", "name": "Grupo BR" }""");
        var throttle = Throttle(interval: 120);
        var waha = Waha(stub);
        var a = Link("AAAAAAAAAAAA");
        var b = Link("BBBBBBBBBBBB");

        // 1ª entrada (T0): entra e registra.
        (await TryJoinAsync(throttle, waha, a, T0)).Should().Be("joined");
        a.Status.Should().Be(GroupLinkStatus.Joined);
        a.WhatsAppGroupId.Should().Be("120363111@g.us");

        // 2ª entrada 10s depois: BLOQUEADA (intervalo de 120s não passou) — nem chama o WAHA.
        (await TryJoinAsync(throttle, waha, b, T0.AddSeconds(10))).Should().Be("blocked");
        b.Status.Should().Be(GroupLinkStatus.Resolved, "não entrou: ficou como estava");

        // Passado o intervalo: libera.
        (await TryJoinAsync(throttle, waha, b, T0.AddSeconds(130))).Should().Be("joined");

        // Só 2 chamadas REAIS de join ao WAHA (a bloqueada não tocou o servidor).
        stub.JoinCalls.Should().Be(2);
        throttle.GetStatus(T0.AddSeconds(130)).JoinsToday.Should().Be(2);
    }

    [Fact]
    public async Task Convite_invalido_4xx_nao_consome_a_trava()
    {
        var stub = new JoinStub(HttpStatusCode.BadRequest, "{}"); // WAHA recusa o convite (4xx)
        var throttle = Throttle();
        var waha = Waha(stub);
        var morto = Link("CCCCCCCCCCCC");

        var r = await TryJoinAsync(throttle, waha, morto, T0);

        r.Should().Be("invalid");
        morto.Status.Should().Be(GroupLinkStatus.Invalid);
        // A trava NÃO foi consumida: nenhuma entrada contabilizada, teto cheio, sem espera.
        var s = throttle.GetStatus(T0);
        s.JoinsToday.Should().Be(0);
        s.Remaining.Should().Be(15);
        s.WaitSeconds.Should().Be(0, "uma entrada que falhou não pode bloquear a próxima tentativa");
    }
}
