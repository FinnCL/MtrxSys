using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.Options;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Application.UseCases.Groups;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using MtrxSys.Infrastructure.Waha;
using NSubstitute;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Collector;

/// <summary>
/// E2E PONTA-A-PONTA do Coletor contra Postgres REAL, com o WahaClient REAL falando com um WAHA
/// simulado (HttpMessageHandler). Cobre o fluxo inteiro do elo que a correção endureceu:
///   cola link → valida (página pública, mockada) → Resolved → ENTRA (join resolve o JID do grupo,
///   nunca o código do convite) → MarkJoined grava o JID → IMPORTA os participantes por esse JID →
///   contatos BR persistidos (próprio número e estrangeiro fora).
/// O ponto crítico provado aqui: o id que sai do join é o que ENTRA no import e gera contatos. Se o
/// bug antigo voltasse (gravar o código do convite como id), o import bateria num JID inexistente e
/// importaria 0 — estes testes quebrariam.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class CollectorPipelineE2ETests : IAsyncLifetime
{
    private const string Session = "default";
    private const string Code = "AAAAAAAAAAAAAAAAAAAA"; // código de convite (base62) — NÃO é id de grupo
    private const string Jid = "120363777@g.us";        // JID real do grupo (o que o import precisa)
    private const string GroupName = "Grupo Marketing BR";
    private const string OwnPhone = "5511999998888";    // número conectado ("me") — não vira contato

    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _db = new MtrxDbContext(new DbContextOptionsBuilder<MtrxDbContext>().UseNpgsql(_pg.GetConnectionString()).Options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 6, 16, 12, 0, 0, TimeSpan.Zero);
    }

    // WAHA simulado: responde join, /groups (fallback de resolução), /sessions (meu número) e
    // /participants (só pro JID certo — qualquer outro JID devolve lista vazia, expondo id errado).
    private sealed class WahaStub(string joinJson, string groupsJson) : HttpMessageHandler
    {
        private const string ParticipantsJson = """
        [
          { "id": "5511999998888@c.us", "pushName": "Eu" },
          { "id": "5511987651234@c.us", "pushName": "Ana" },
          { "id": "5521987654321@c.us", "pushName": "Bruno" },
          { "id": "551100@c.us",        "pushName": "Lixo" }
        ]
        """;
        private const string SessionJson = """{ "status": "WORKING", "me": { "id": "5511999998888@c.us", "pushName": "Eu" } }""";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            string json =
                request.Method == HttpMethod.Post && path.EndsWith($"/{Session}/groups/join", StringComparison.Ordinal) ? joinJson
                : path == $"/api/{Session}/groups" ? groupsJson
                : path == $"/api/sessions/{Session}" ? SessionJson
                : path.EndsWith("/participants", StringComparison.Ordinal)
                    ? (path.Contains($"/groups/{Jid}/participants", StringComparison.Ordinal) ? ParticipantsJson : "[]")
                : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        }
    }

    private WahaClient BuildWaha(string joinJson, string groupsJson) =>
        new(new HttpClient(new WahaStub(joinJson, groupsJson)) { BaseAddress = new Uri("http://waha.test/") },
            Options.Create(new WahaOptions()));

    private CollectGroupLinksUseCase BuildCollector()
    {
        var validator = Substitute.For<IWhatsAppInviteValidator>();
        validator.CheckAsync(Code, Arg.Any<CancellationToken>()).Returns(new InviteCheck(true, GroupName));
        return new CollectGroupLinksUseCase(
            Substitute.For<IGroupLinkSource>(),
            Substitute.For<IGroupLinkSearchSource>(),
            validator,
            new GroupLinkRepository(_db),
            new UnitOfWork(_db),
            new FixedClock(),
            Substitute.For<IRandomSource>(),
            Options.Create(new CollectorOptions()));
    }

    // Reproduz o miolo do endpoint POST /links/{code}/join (sem o HTTP): entra e grava o JID.
    private async Task<string?> JoinAsync(WahaClient waha)
    {
        var repo = new GroupLinkRepository(_db);
        var uow = new UnitOfWork(_db);
        var link = await repo.GetByCodeAsync(Code, CancellationToken.None);
        var joined = await waha.JoinGroupByInviteAsync(Session, link!.InviteCode, CancellationToken.None);
        link.MarkJoined(joined.Id, new DateTimeOffset(2026, 6, 16, 12, 0, 0, TimeSpan.Zero));
        await repo.UpdateAsync(link, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        return link.WhatsAppGroupId;
    }

    private async Task<ImportResult> ImportAsync(WahaClient waha, string groupId)
    {
        var useCase = new ImportGroupMembersUseCase(
            waha,
            new ContactRepository(_db),
            new UnitOfWork(_db),
            new FixedClock(),
            new BrazilPhoneValidator(),
            Options.Create(new DispatchOptions { SessionId = Session, OnlyBrazilianContacts = true }));
        return await useCase.ExecuteAsync(groupId, GroupName, CancellationToken.None);
    }

    private async Task AssertImportedContactsAsync()
    {
        _db.ChangeTracker.Clear();
        var saved = await _db.Contacts.Where(c => c.GroupTag == GroupName).ToListAsync();
        var phones = saved.Select(c => c.Phone.E164).ToList();
        phones.Should().BeEquivalentTo(["+5511987651234", "+5521987654321"]);
        phones.Should().NotContain("+5511999998888", "o próprio número não vira contato");
        phones.Should().NotContain("+551100", "número inválido é barrado pela validação");
    }

    [Fact]
    public async Task Fluxo_completo_join_com_jid_direto_ate_importar_contatos()
    {
        // 1) Cola o link → valida (vivo) → Resolved.
        var manual = await BuildCollector().AddManualAsync(
            [$"https://chat.whatsapp.com/{Code}"], "marketing", CancellationToken.None);
        manual.Live.Should().Be(1);
        _db.ChangeTracker.Clear();
        (await _db.GroupLinks.SingleAsync(g => g.InviteCode == Code)).Status.Should().Be(GroupLinkStatus.Resolved);

        // 2) Entra: o join devolve o JID direto → grava o JID (não o código do convite).
        var waha = BuildWaha(joinJson: $$"""{ "id": "{{Jid}}", "name": "{{GroupName}}" }""", groupsJson: "{}");
        var storedId = await JoinAsync(waha);
        storedId.Should().Be(Jid, "o id gravado tem de ser o JID do grupo, nunca o código do convite");
        storedId.Should().NotBe(Code);
        _db.ChangeTracker.Clear();
        (await _db.GroupLinks.SingleAsync(g => g.InviteCode == Code)).Status.Should().Be(GroupLinkStatus.Joined);

        // 3) Importa pelo JID gravado → só os participantes BR (sem o próprio número, sem estrangeiro).
        var result = await ImportAsync(waha, storedId!);
        result.Imported.Should().Be(2);
        result.Total.Should().Be(4);
        await AssertImportedContactsAsync();
    }

    [Fact]
    public async Task Fluxo_completo_join_sem_id_resolve_jid_pelo_groups_e_importa()
    {
        // 1) Resolved (igual acima).
        await BuildCollector().AddManualAsync([$"chat.whatsapp.com/{Code}"], "marketing", CancellationToken.None);
        _db.ChangeTracker.Clear();

        // 2) join SEM id (resposta mínima) → resolve o JID pelo /groups (NOWEB: objeto por JID).
        var groupsJson = $$"""{ "{{Jid}}": { "id": "{{Jid}}", "subject": "{{GroupName}}", "participants": [] } }""";
        var waha = BuildWaha(joinJson: $$"""{ "name": "{{GroupName}}" }""", groupsJson: groupsJson);
        var storedId = await JoinAsync(waha);
        storedId.Should().Be("120363777", "resolveu o número do grupo (sem @g.us) pelo nome no /groups");

        // 3) Import (EnsureGroupJid recompõe o @g.us) → mesmos 2 contatos BR.
        var result = await ImportAsync(waha, storedId!);
        result.Imported.Should().Be(2);
        await AssertImportedContactsAsync();
    }
}
