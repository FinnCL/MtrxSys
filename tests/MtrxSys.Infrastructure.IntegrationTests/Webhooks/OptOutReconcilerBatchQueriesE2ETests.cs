using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Validation;
using MtrxSys.Infrastructure.Persistence;
using MtrxSys.Infrastructure.Persistence.Repositories;
using Testcontainers.PostgreSql;

namespace MtrxSys.Infrastructure.IntegrationTests.Webhooks;

/// <summary>Prova, contra Postgres REAL, que as consultas EM LOTE do <c>OptOutReconciler</c> traduzem
/// pra SQL e devolvem o mesmo recorte da versão um-a-um que substituíram (N+1). São dois
/// <c>Contains</c> sobre coluna — um deles sobre coluna ANULÁVEL (<c>contact_id</c>) — que só falhariam
/// em runtime; e o <c>MigrateAsync</c> aqui também exercita a migração nova (reactivated_at).</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design", "CA1001", Justification = "Descarte de _db/_pg em IAsyncLifetime.DisposeAsync.")]
public sealed class OptOutReconcilerBatchQueriesE2ETests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _pg = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    private MtrxDbContext _db = null!;
    private readonly BrazilPhoneValidator _phones = new();
    private static readonly DateTimeOffset T0 = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        await _pg.StartAsync();
        var options = new DbContextOptionsBuilder<MtrxDbContext>().UseNpgsql(_pg.GetConnectionString()).Options;
        _db = new MtrxDbContext(options);
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _pg.DisposeAsync();
    }

    private async Task<Contact> SeedContactAsync(string digits)
    {
        var c = Contact.Create(Guid.NewGuid(), _phones.Validate(digits).Value!, name: null,
            groupTag: null, theme: null, optInAt: T0);
        _db.Contacts.Add(c);
        await _db.SaveChangesAsync();
        return c;
    }

    private async Task<Conversation> SeedConversationAsync(Guid? contactId, string waChatId, bool isGroup, DateTimeOffset createdAt)
    {
        var conv = Conversation.Create(Guid.NewGuid(), waChatId, contactId, title: null, isGroup, createdAt);
        _db.Conversations.Add(conv);
        await _db.SaveChangesAsync();
        return conv;
    }

    private async Task SeedMessageAsync(Guid conversationId, MessageDirection direction, string body, DateTimeOffset at)
    {
        _db.ChatMessages.Add(ChatMessage.Create(
            Guid.NewGuid(), conversationId, $"wamid.{Guid.NewGuid():N}", direction, null, body, at));
        await _db.SaveChangesAsync();
    }

    [Fact]
    public async Task Conversas_em_lote_trazem_so_as_individuais_dos_contatos_pedidos()
    {
        var wanted = await SeedContactAsync("11999990001");
        var other = await SeedContactAsync("11999990002");
        var individual = await SeedConversationAsync(wanted.Id, "5511999990001@c.us", isGroup: false, T0);
        // Ruído que NÃO pode voltar: grupo do mesmo contato, conversa de outro contato e conversa órfã.
        await SeedConversationAsync(wanted.Id, "123-group@g.us", isGroup: true, T0);
        await SeedConversationAsync(other.Id, "5511999990002@c.us", isGroup: false, T0);
        await SeedConversationAsync(null, "5511999990003@c.us", isGroup: false, T0);

        var repo = new ConversationRepository(_db);
        var found = await repo.ListIndividualByContactIdsAsync([wanted.Id], CancellationToken.None);

        found.Should().ContainSingle();
        found[0].ConversationId.Should().Be(individual.Id);
        found[0].ContactId.Should().Be(wanted.Id);
        // LastActivityAt = LastMessageAt ?? CreatedAt (COALESCE no SQL). Sem mensagem, cai no CreatedAt.
        found[0].LastActivityAt.Should().Be(T0);
        (await repo.ListIndividualByContactIdsAsync([], CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task Inbound_em_lote_traz_so_o_recebido_das_conversas_pedidas()
    {
        var contact = await SeedContactAsync("11999990010");
        var conv = await SeedConversationAsync(contact.Id, "5511999990010@c.us", isGroup: false, T0);
        var otherConv = await SeedConversationAsync(null, "5511999990011@c.us", isGroup: false, T0);
        await SeedMessageAsync(conv.Id, MessageDirection.Inbound, "sair", T0.AddMinutes(1));
        await SeedMessageAsync(conv.Id, MessageDirection.Outbound, "oi, tudo bem?", T0.AddMinutes(2));
        await SeedMessageAsync(otherConv.Id, MessageDirection.Inbound, "de outra conversa", T0.AddMinutes(3));

        var repo = new ChatMessageRepository(_db);
        var found = await repo.ListInboundByConversationsAsync([conv.Id], CancellationToken.None);

        found.Should().ContainSingle();
        found[0].ConversationId.Should().Be(conv.Id);
        found[0].Body.Should().Be("sair");
        found[0].Timestamp.Should().Be(T0.AddMinutes(1));
        (await repo.ListInboundByConversationsAsync([], CancellationToken.None)).Should().BeEmpty();
    }
}
