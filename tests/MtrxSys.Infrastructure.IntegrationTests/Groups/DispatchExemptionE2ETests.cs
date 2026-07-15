using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Groups;

/// <summary>
/// E2E da ISENÇÃO de disparo contra um Postgres REAL (Testcontainers). Vale contra banco real e não
/// mock por dois motivos: a cláusula do isento é um <c>Contains</c> dentro de um <c>Where</c> que o
/// EF pode falhar em TRADUZIR só em runtime; e o que se está afirmando aqui é o PÚBLICO — o mesmo
/// ListByFilterAsync que o /api/dispatch usa pra decidir pra quem a mensagem sai.
///
/// O que estes testes cravam:
///   - isento com LastSentAt preenchido VOLTA ao público (é a razão da isenção existir);
///   - não-isento com LastSentAt preenchido continua fora (a trava segue de pé pra todo o resto);
///   - isento em OPT-OUT continua fora (a isenção nunca alcança quem pediu pra sair);
///   - isento que já está na FILA continua fora (proteção contra clique repetido, não é a trava de
///     "uma vez só");
///   - isenção DESLIGADA (o default, e o estado da produção hoje) não muda nada.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001",
    Justification = "Descarte de _db/_pg é feito em IAsyncLifetime.DisposeAsync, que o analisador não reconhece.")]
public sealed class DispatchExemptionE2ETests : IAsyncLifetime
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

    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);

    private const string Friend = "+5571999998888";
    private const string Stranger = "+5571977776666";

    // Contato que JÁ RECEBEU disparo — o estado que a trava do LastSentAt bloqueia. Normaliza com
    // NormalizeTrusted porque é o que a IMPORTAÇÃO DE GRUPO faz (ImportGroupMembersUseCase): estes
    // contatos nascem de lá, e o teste tem que reproduzir a forma real do telefone no banco.
    private async Task<Contact> SeedAlreadySentAsync(string phone)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.NormalizeTrusted(phone), "Fulano", "Avulsos", null, Now);
        c.RegisterSend(Now);
        _db.Contacts.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    // Grupo criado pelo operador, com a isenção LIGADA sobre os telefones dados.
    private async Task SeedExemptGroupAsync(params string[] phones)
    {
        var g = OwnedGroup.Create(Guid.NewGuid(), "120363410949918818", "Amigos", Now);
        g.EnableDispatchExemption(phones, Guid.NewGuid);
        _db.OwnedGroups.Add(g);
        await _db.SaveChangesAsync();
    }

    private async Task<List<string>> AudienceAsync()
    {
        var repo = new ContactRepository(_db);
        var owned = new OwnedGroupRepository(_db);
        var exempt = await owned.ListExemptPhonesAsync(CancellationToken.None);
        var targets = await repo.ListByFilterAsync(
            new ContactFilter(ExcludeAlreadyDispatched: true, ExemptPhonesE164: exempt),
            CancellationToken.None);
        return targets.Select(c => c.Phone.E164).ToList();
    }

    [Fact]
    public async Task Isento_que_ja_recebeu_volta_ao_publico_e_o_resto_continua_travado()
    {
        await SeedAlreadySentAsync(Friend);
        await SeedAlreadySentAsync(Stranger);
        await SeedExemptGroupAsync(Friend);

        var audience = await AudienceAsync();

        audience.Should().Contain(Friend, "é a razão da isenção existir: conversa recorrente com conhecido");
        audience.Should().NotContain(Stranger, "a trava de 'já enviei pra esse' continua valendo pra todo o resto");
    }

    [Fact]
    public async Task Isenção_desligada_nao_muda_nada()
    {
        await SeedAlreadySentAsync(Friend);
        // Grupo criado, mas a chave NUNCA foi ligada — o default, e o estado de toda a produção hoje.
        var g = OwnedGroup.Create(Guid.NewGuid(), "120363410949918818", "Amigos", Now);
        _db.OwnedGroups.Add(g);
        await _db.SaveChangesAsync();

        var audience = await AudienceAsync();

        audience.Should().NotContain(Friend, "sem a chave ligada o disparo se comporta exatamente como antes");
    }

    [Fact]
    public async Task Isento_em_opt_out_continua_fora()
    {
        var c = await SeedAlreadySentAsync(Friend);
        c.OptOut(Now);
        await _db.SaveChangesAsync();
        await SeedExemptGroupAsync(Friend);

        var audience = await AudienceAsync();

        audience.Should().NotContain(Friend,
            "a isenção dispensa a trava de 'já falei com esse', NUNCA o pedido de sair — e num grupo "
            + "de conhecidos respeitar isso importa mais, não menos");
    }

    [Fact]
    public async Task Isento_que_ja_esta_na_fila_nao_e_enfileirado_de_novo()
    {
        var c = await SeedAlreadySentAsync(Friend);
        _db.DispatchJobs.Add(DispatchJob.Schedule(Guid.NewGuid(), c.Id, Guid.NewGuid(), Now));
        await _db.SaveChangesAsync();
        await SeedExemptGroupAsync(Friend);

        var audience = await AudienceAsync();

        audience.Should().NotContain(Friend,
            "a trava de 'já está na fila' protege contra o mesmo clique enfileirar a pessoa duas "
            + "vezes — não é a regra de 'uma vez só', então a isenção não a dispensa");
    }

    [Fact]
    public async Task Numero_legado_sem_o_9o_digito_e_isentado_na_mesma_forma_que_o_contato()
    {
        // Número BR legado, SEM o 9º dígito — a libphonenumber o REJEITA (não o corrige), e por isso
        // o NormalizeTrusted o preserva como veio, tanto ao virar Contact (importação do grupo)
        // quanto ao entrar na fotografia da isenção (endpoint).
        //
        // O que este teste protege: as duas pontas usarem a MESMA função. Se o endpoint guardasse o
        // número cru e a lib normalizasse a string (hoje ela não normaliza esta, mas normaliza
        // outras), a isenção falharia EM SILÊNCIO — chave acesa e o disparo pulando a pessoa igual.
        const string legacy = "+557188887777";
        _phones.Validate(legacy).IsSuccess.Should().BeFalse("é justamente o número que a lib rejeita");

        var contact = await SeedAlreadySentAsync(legacy);
        // Exatamente o que o endpoint faz com o que o WAHA devolve.
        await SeedExemptGroupAsync(_phones.NormalizeTrusted(legacy).E164);

        (await AudienceAsync()).Should().Contain(contact.Phone.E164,
            "contato e isenção saem da mesma string do WAHA pela mesma função — têm que casar");
    }

    // Reproduz o caminho EXATO do endpoint PATCH /exemption, que os outros testes não cobriam: o
    // grupo JÁ existe (foi declarado antes), é carregado RASTREADO com os membros, e só então a
    // isenção é ligada. Os outros testes ligavam a isenção num grupo NOVO (insert) — caminho
    // diferente no EF. Números reais de um grupo de produção: BR no formato ANTIGO, sem o 9º dígito,
    // que é como o WhatsApp de fato os devolve.
    [Fact]
    public async Task Ligar_a_isencao_num_grupo_ja_declarado_persiste_os_membros()
    {
        var real = new[]
        {
            "+557182368724", "+557184731714", "+557185211291",
            "+557186576422", "+557191072835", "+557193836443",
        };
        // 1) o operador clica "Este grupo é meu" → grava só o grupo, sem membros.
        _db.OwnedGroups.Add(OwnedGroup.Create(Guid.NewGuid(), "120363411066030182", "Grupo teste Finn aqui!", Now));
        await _db.SaveChangesAsync();
        _db.ChangeTracker.Clear(); // requisição nova: nada rastreado, como no endpoint

        // 2) o operador marca a caixa → o endpoint faz exatamente isto.
        var owned = new OwnedGroupRepository(_db);
        var target = await owned.GetForUpdateAsync("120363411066030182", CancellationToken.None);
        target.Should().NotBeNull();
        var normalized = real.Select(p => _phones.NormalizeTrusted(p).E164);
        target!.EnableDispatchExemption(normalized, Guid.NewGuid);
        await _db.SaveChangesAsync();

        (await owned.ListExemptPhonesAsync(CancellationToken.None)).Should().HaveCount(6);
    }

    // POSSE = ISENÇÃO. Declarar "este grupo é meu" já libera o envio repetido pros membros, num ato
    // só: o grupo é de conhecidos, é pra isso que ele existe, e pedir a mesma afirmação duas vezes
    // era cerimônia. Este teste é o contrato — se alguém separar de novo os dois atos, ele quebra.
    [Fact]
    public async Task Marcar_como_meu_ja_libera_o_envio_repetido()
    {
        await SeedAlreadySentAsync(Friend);   // já recebeu: a trava do LastSentAt o barraria
        await SeedAlreadySentAsync(Stranger); // idem, e este NÃO está no grupo
        (await AudienceAsync()).Should().BeEmpty("sanidade: sem posse, os dois estão travados");

        // O que o endpoint POST /claim faz: registra a posse E fotografa os membros, junto.
        var target = OwnedGroup.Create(Guid.NewGuid(), "120363411066030182", "Amigos", Now);
        target.EnableDispatchExemption([Friend], Guid.NewGuid);
        _db.OwnedGroups.Add(target);
        await _db.SaveChangesAsync();

        var audience = await AudienceAsync();
        audience.Should().Contain(Friend, "marcar como meu, sozinho, já tem que liberar");
        audience.Should().NotContain(Stranger, "quem não está no grupo não é alcançado pela marca");
    }

    [Fact]
    public async Task Desfazer_a_posse_derruba_a_isencao_junto()
    {
        // Desmarcar "é meu" tem que levar a isenção embora. Um grupo que não é seu não pode ter
        // dispensa, e ela sumiria da tela (a caixa só aparece em grupo seu) — ficaria uma dispensa
        // órfã, ativa no disparo e invisível pra desligar. Quem garante isso é o cascade da FK.
        await SeedAlreadySentAsync(Friend);
        await SeedExemptGroupAsync(Friend);
        var owned = new OwnedGroupRepository(_db);
        (await AudienceAsync()).Should().Contain(Friend, "sanidade: a isenção está valendo antes");

        (await owned.RemoveAsync("120363410949918818", CancellationToken.None)).Should().BeTrue();
        await _db.SaveChangesAsync();

        (await _db.Set<OwnedGroupMember>().CountAsync()).Should().Be(0, "cascade apaga a fotografia");
        (await AudienceAsync()).Should().NotContain(Friend, "sem posse não há isenção");
    }

    [Fact]
    public async Task Desligar_esquece_a_fotografia_dos_membros()
    {
        await SeedAlreadySentAsync(Friend);
        await SeedExemptGroupAsync(Friend);
        var owned = new OwnedGroupRepository(_db);

        var g = await owned.GetForUpdateAsync("120363410949918818", CancellationToken.None);
        g!.DisableDispatchExemption();
        await _db.SaveChangesAsync();

        (await owned.ListExemptPhonesAsync(CancellationToken.None)).Should().BeEmpty();
        (await AudienceAsync()).Should().NotContain(Friend);
        // A lista velha não pode ficar guardada esperando um religar sem conferência.
        (await _db.Set<OwnedGroupMember>().CountAsync()).Should().Be(0);
    }
}

