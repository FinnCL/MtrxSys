using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Application.Abstractions;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Contacts;

/// <summary>
/// E2E do "apagar permanentemente" (purge seguro + hard delete total) contra Postgres REAL. Prova o
/// que o compilador NÃO garante: (1) o EF TRADUZ o ExecuteDelete com subconsulta EXISTS no conjunto-
/// alvo (falharia só em runtime se não traduzisse); (2) as filhas COM FK caem por cascata
/// (contact_notes/stage_changes/tag_assignments pelo contato; chat_messages pela conversation); e
/// (3) as filhas SEM FK (dispatch_jobs, conversations) são apagadas à mão — NÃO sobra órfão. O purge
/// ainda preserva quem deu opt-out como soft delete (anti-ban). Exige Docker rodando.
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001",
    Justification = "Descarte de _db/_pg é feito em IAsyncLifetime.DisposeAsync, que o analisador não reconhece.")]
public sealed class HardDeleteByGroupE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();
    private static readonly CancellationToken Ct = CancellationToken.None;
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        _db = new MtrxDbContext(new DbContextOptionsBuilder<MtrxDbContext>()
            .UseNpgsql(_pg.GetConnectionString())
            .Options);
        await _db.Database.MigrateAsync();
        // A tag precisa existir antes da atribuição (FK contact_tag_assignments → contact_tags).
        _db.ContactTags.Add(ContactTag.Create("vip", null, Now));
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    // Semeia um contato com TODAS as linhas-filhas: job de disparo, conversa + mensagem, nota,
    // histórico de estágio e atribuição de tag. Devolve o id do contato.
    private async Task<Guid> SeedContactWithChildrenAsync(string phone, string group, bool optOut)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.Validate(phone).Value!,
            name: phone, groupTag: group, theme: null, optInAt: Now);
        if (optOut)
        {
            c.OptOut(Now);
        }
        _db.Contacts.Add(c);

        // Sem FK pro contato → tem que ser apagado à mão no hard delete.
        _db.DispatchJobs.Add(DispatchJob.Schedule(Guid.NewGuid(), c.Id, Guid.NewGuid(), Now));

        // Conversa (sem FK pro contato) + mensagem (FK cascade pra conversa).
        var conv = Conversation.Create(Guid.NewGuid(), waChatId: $"{phone}@c.us",
            contactId: c.Id, title: phone, isGroup: false, createdAt: Now);
        _db.Conversations.Add(conv);
        _db.ChatMessages.Add(ChatMessage.Create(Guid.NewGuid(), conv.Id, waMessageId: $"msg-{phone}",
            MessageDirection.Inbound, authorPhone: null, body: "oi", timestamp: Now));

        // Filhas com FK cascade pro contato.
        _db.ContactNotes.Add(ContactNote.Create(Guid.NewGuid(), c.Id, "nota", Now, Guid.Empty));
        _db.ContactStageChanges.Add(ContactStageChange.Create(Guid.NewGuid(), c.Id, null, ContactStage.Lead, Now, Guid.Empty));
        _db.ContactTagAssignments.Add(ContactTagAssignment.Create(c.Id, "vip", Now));

        await _db.SaveChangesAsync(Ct);
        return c.Id;
    }

    private async Task AssertNoRowsForContactAsync(Guid id)
    {
        (await _db.DispatchJobs.CountAsync(j => j.ContactId == id, Ct)).Should().Be(0, "dispatch_jobs (sem FK) tem que ser apagado à mão");
        (await _db.Conversations.CountAsync(cv => cv.ContactId == id, Ct)).Should().Be(0, "conversations (sem FK) tem que ser apagado à mão");
        (await _db.ContactNotes.CountAsync(n => n.ContactId == id, Ct)).Should().Be(0, "contact_notes cai por cascata");
        (await _db.ContactStageChanges.CountAsync(s => s.ContactId == id, Ct)).Should().Be(0, "contact_stage_changes cai por cascata");
        (await _db.ContactTagAssignments.CountAsync(t => t.ContactId == id, Ct)).Should().Be(0, "contact_tag_assignments cai por cascata");
    }

    private async Task AssertChildrenPresentAsync(Guid id)
    {
        (await _db.DispatchJobs.CountAsync(j => j.ContactId == id, Ct)).Should().Be(1);
        (await _db.Conversations.CountAsync(cv => cv.ContactId == id, Ct)).Should().Be(1);
        (await _db.ContactNotes.CountAsync(n => n.ContactId == id, Ct)).Should().Be(1);
        (await _db.ContactStageChanges.CountAsync(s => s.ContactId == id, Ct)).Should().Be(1);
        (await _db.ContactTagAssignments.CountAsync(t => t.ContactId == id, Ct)).Should().Be(1);
    }

    // Invariante global: nenhuma mensagem aponta pra uma conversa que não existe mais (prova a
    // cascata conversa→mensagens).
    private async Task AssertNoOrphanMessagesAsync() =>
        (await _db.ChatMessages.CountAsync(m => !_db.Conversations.Any(cv => cv.Id == m.ConversationId), Ct))
            .Should().Be(0, "chat_messages devem cair por cascata ao apagar a conversation");

    [Fact]
    public async Task Purge_apaga_de_vez_quem_nao_tem_optout_e_preserva_quem_saiu_como_soft_delete()
    {
        var repo = new ContactRepository(_db);
        var aId = await SeedContactWithChildrenAsync("11955550001", "G", optOut: false); // apagar
        var bId = await SeedContactWithChildrenAsync("11955550002", "G", optOut: true);  // preservar (soft)
        var cId = await SeedContactWithChildrenAsync("11955559999", "Outro", optOut: false); // controle

        var (purged, keptOptedOut) = await repo.PurgeByGroupTagAsync("G", Now, Ct);

        purged.Should().Be(1);
        keptOptedOut.Should().Be(1);

        _db.ChangeTracker.Clear();
        // A (sem opt-out): apagado FISICAMENTE, com todas as filhas.
        (await _db.Contacts.AnyAsync(c => c.Id == aId, Ct)).Should().BeFalse();
        await AssertNoRowsForContactAsync(aId);
        // B (opt-out): NÃO apagado — só soft delete; a linha e as filhas continuam (supressão anti-ban).
        var b = await _db.Contacts.AsNoTracking().SingleAsync(c => c.Id == bId, Ct);
        b.DeletedAt.Should().NotBeNull();
        b.OptOutAt.Should().NotBeNull();
        await AssertChildrenPresentAsync(bId);
        // C (outro grupo): intacto.
        var c = await _db.Contacts.AsNoTracking().SingleAsync(x => x.Id == cId, Ct);
        c.DeletedAt.Should().BeNull();
        await AssertChildrenPresentAsync(cId);
        // Nenhuma mensagem órfã (a conversa do A caiu e levou a mensagem junto).
        await AssertNoOrphanMessagesAsync();
    }

    [Fact]
    public async Task HardDelete_apaga_tudo_do_grupo_inclusive_optout_e_nao_deixa_orfaos()
    {
        var repo = new ContactRepository(_db);
        var aId = await SeedContactWithChildrenAsync("11955550001", "G", optOut: false);
        var bId = await SeedContactWithChildrenAsync("11955550002", "G", optOut: true);
        var cId = await SeedContactWithChildrenAsync("11955559999", "Outro", optOut: false); // controle

        var deleted = await repo.HardDeleteByGroupTagAsync("G", Ct);

        deleted.Should().Be(2, "A e B (inclusive quem deu opt-out) são apagados");

        _db.ChangeTracker.Clear();
        (await _db.Contacts.AnyAsync(c => c.Id == aId || c.Id == bId, Ct)).Should().BeFalse();
        await AssertNoRowsForContactAsync(aId);
        await AssertNoRowsForContactAsync(bId);
        // Controle intacto.
        (await _db.Contacts.AnyAsync(c => c.Id == cId, Ct)).Should().BeTrue();
        await AssertChildrenPresentAsync(cId);
        await AssertNoOrphanMessagesAsync();
    }
}
