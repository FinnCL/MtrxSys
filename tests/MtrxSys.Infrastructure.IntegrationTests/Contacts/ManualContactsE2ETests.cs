using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Application.UseCases.Contacts;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Contacts;

/// <summary>
/// E2E do cadastro manual contra um Postgres REAL (Testcontainers): valida o caminho que o repo
/// mockado dos testes de unidade não cobre — índice único de verdade, reativação de soft-delete no
/// EF real, dedup canônica+legada (anti-disparo-duplicado) e a inclusão no PÚBLICO DE DISPARO real
/// (o mesmo ListByFilterAsync que o endpoint /api/dispatch usa). Exige Docker rodando.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001",
    Justification = "Descarte de _db/_pg é feito em IAsyncLifetime.DisposeAsync, que o analisador não reconhece.")]
public sealed class ManualContactsE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        var options = new DbContextOptionsBuilder<MtrxDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options;
        _db = new MtrxDbContext(options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 6, 10, 12, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task ListOptedOutPhones_returns_opted_out_including_discarded()
    {
        // Cobre a query nova do backfill do registro (projeção do owned Phone.E164 — EF pode falhar
        // em TRADUZIR isso só em runtime) + a semântica: opt-out de contato DESCARTADO ainda conta.
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var active = Contact.Create(Guid.NewGuid(), _phones.Validate("11955550001").Value!,
            name: "Ativo", groupTag: "G", theme: null, optInAt: now);
        var optedOut = Contact.Create(Guid.NewGuid(), _phones.Validate("11955550002").Value!,
            name: "Saiu", groupTag: "G", theme: null, optInAt: now);
        optedOut.OptOut(now);
        var optedOutDiscarded = Contact.Create(Guid.NewGuid(), _phones.Validate("11955550003").Value!,
            name: "Saiu e descartado", groupTag: "Reciclados", theme: null, optInAt: now);
        optedOutDiscarded.OptOut(now);
        await repo.AddAsync(active, CancellationToken.None);
        await repo.AddAsync(optedOut, CancellationToken.None);
        await repo.AddAsync(optedOutDiscarded, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        await repo.DiscardByGroupTagAsync("Reciclados", now, CancellationToken.None); // soft delete real

        var phones = await repo.ListOptedOutPhonesAsync(CancellationToken.None);

        phones.Should().BeEquivalentTo(new[] { optedOut.Phone.E164, optedOutDiscarded.Phone.E164 });
    }

    // 🔴 A coluna GERADA phone_digits só se prova contra Postgres real: o EF NÃO pode listá-la no
    // INSERT (o Postgres recusa GENERATED ALWAYS), e a query de dedup tem que casar por dígitos
    // ignorando o formato. Se qualquer um dos dois quebrar, é aqui — não num teste em memória.
    [Fact]
    public async Task GetByPhoneDigits_casa_formatos_diferentes_e_o_insert_nao_escreve_a_coluna_gerada()
    {
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // Grava um contato válido (11 dígitos com o 9º). O E164 fica "+5511955550009".
        var contato = Contact.Create(Guid.NewGuid(), _phones.Validate("11955550009").Value!,
            name: null, groupTag: "G", theme: null, optInAt: now);
        await repo.AddAsync(contato, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None); // se o EF escrevesse phone_digits, estouraria

        contato.Phone.E164.Should().StartWith("+"); // confirma que o gravado TEM o "+"
        var digitos = new string([.. contato.Phone.E164.Where(char.IsDigit)]); // "5511955550009", sem "+"

        // Procura só pelos DÍGITOS (sem "+"): tem que achar o mesmo contato pela coluna gerada,
        // provando que o casamento ignora o formato. Se a coluna gerada não estivesse populada (ex.:
        // o EF a marcando como coluna comum), phone_digits viria NULL e isto não acharia nada.
        var achado = await repo.GetByPhoneDigitsAsync([digitos], CancellationToken.None);

        achado.Should().ContainKey(digitos);
        achado[digitos].Id.Should().Be(contato.Id);
    }

    [Fact]
    public async Task Full_flow_persists_dedupes_and_lands_in_dispatch_audience()
    {
        var repo = new ContactRepository(_db);
        var uow = new UnitOfWork(_db);
        var useCase = new AddManualContactsUseCase(repo, uow, new FixedClock(), _phones);
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

        // --- Seed (estado pré-existente no banco) ---
        // (1) Contato LEGADO sem o 9, como o import de grupo salva (NormalizeTrusted mantém o cru).
        var legacy = Contact.Create(
            Guid.NewGuid(), _phones.NormalizeTrusted("+551187654321"),
            name: "Legado", groupTag: "Vendas", theme: null, optInAt: now);
        // (2) Contato DESCARTADO (soft delete) que será re-adicionado manualmente.
        var discarded = Contact.Create(
            Guid.NewGuid(), _phones.Validate("11955554444").Value!,
            name: "Reciclado", groupTag: "Reciclados", theme: null, optInAt: now);
        await repo.AddAsync(legacy, CancellationToken.None);
        await repo.AddAsync(discarded, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        // Descarta o grupo "Reciclados" (ExecuteUpdate, igual à tela) → soft delete real no banco.
        await repo.DiscardByGroupTagAsync("Reciclados", now, CancellationToken.None);
        // Limpa o tracker pra as queries seguintes lerem o estado REAL do banco (o ExecuteUpdate
        // não atualiza entidades já rastreadas — sem isso o use case veria DeletedAt desatualizado).
        _db.ChangeTracker.Clear();

        // --- Ação: colar uma lista mista ---
        var pasted = new[]
        {
            "11987654321",   // forma moderna do LEGADO (+551187654321) → deve casar via variante sem 9
            "31988887777",   // novo celular válido
            "3188887766",    // celular antigo sem o 9 → auto-corrige pra +5531988887766
            "11955554444",   // o DESCARTADO → deve reativar (DeletedAt volta a null)
            "999",           // inválido (sem DDD)
            "31988887777",   // repetido no mesmo lote → duplicado
        };
        var result = await useCase.ExecuteAsync(pasted, null, CancellationToken.None);

        // --- Resultado por linha ---
        result.Total.Should().Be(6);
        result.Added.Should().Be(2);       // 31988887777 e 3188887766
        result.Corrected.Should().Be(1);   // 3188887766 (9º dígito)
        result.Duplicated.Should().Be(3);  // legado, descartado, repetido
        result.Invalid.Should().Be(1);     // 999

        result.Lines[0].Status.Should().Be(ManualLineStatus.Duplicate);
        result.Lines[0].Phone.Should().Be("+551187654321"); // mostra a forma salva (sem o 9)
        result.Lines[2].Status.Should().Be(ManualLineStatus.Corrected);
        result.Lines[2].Phone.Should().Be("+5531988887766");
        result.Lines[4].Status.Should().Be(ManualLineStatus.Invalid);
        result.Lines[5].Status.Should().Be(ManualLineStatus.Duplicate);

        // --- Estado persistido ---
        _db.ChangeTracker.Clear();
        // Anti-disparo-duplicado: o legado NÃO ganhou um gêmeo com o 9; só existe a forma sem 9.
        var legacyForms = await _db.Contacts
            .Where(c => c.Phone.E164 == "+551187654321" || c.Phone.E164 == "+5511987654321")
            .ToListAsync();
        legacyForms.Should().ContainSingle().Which.Phone.E164.Should().Be("+551187654321");

        // O descartado foi reativado (soft delete desfeito).
        var reactivated = await _db.Contacts.SingleAsync(c => c.Phone.E164 == "+5511955554444");
        reactivated.DeletedAt.Should().BeNull();

        // --- Inclusão no PÚBLICO DE DISPARO (mesmo filtro do endpoint /api/dispatch) ---
        var audienceFilter = new ContactFilter(
            Stage: null,
            TagName: null,
            GroupTag: null,            // "Todos os grupos"
            ExcludeOptedOut: true,
            EngagedOnly: false,
            ExcludePhoneE164: null,
            ExcludeAlreadyDispatched: true);
        var audience = await repo.ListByFilterAsync(audienceFilter, CancellationToken.None);
        var audiencePhones = audience.Select(c => c.Phone.E164).ToList();

        // Os novos, o corrigido, o reativado e o legado entram no disparo — cada um UMA vez.
        audiencePhones.Should().Contain("+5531988887777");
        audiencePhones.Should().Contain("+5531988887766");
        audiencePhones.Should().Contain("+5511955554444");
        audiencePhones.Should().Contain("+551187654321");
        audiencePhones.Should().OnlyHaveUniqueItems();
        // E nenhum "fantasma" com o 9 do legado.
        audiencePhones.Should().NotContain("+5511987654321");
    }
}
