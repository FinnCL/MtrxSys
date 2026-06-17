using Microsoft.EntityFrameworkCore;
using MtrxSys.Core.Domain.Campaigns;
using MtrxSys.Core.Domain.Contacts;
using MtrxSys.Core.Domain.Conversations;
using MtrxSys.Core.Domain.Groups;
using MtrxSys.Core.Domain.Messages;
using MtrxSys.Core.Domain.SystemState;
using MtrxSys.Core.Domain.Users;

namespace MtrxSys.Infrastructure.Persistence;

public sealed class MtrxDbContext(DbContextOptions<MtrxDbContext> options) : DbContext(options)
{
    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<MessageTemplate> MessageTemplates => Set<MessageTemplate>();
    public DbSet<DispatchJob> DispatchJobs => Set<DispatchJob>();
    public DbSet<SystemStateAggregate> SystemState => Set<SystemStateAggregate>();
    public DbSet<DailySendCount> DailySendCounts => Set<DailySendCount>();
    public DbSet<SendAuditEntry> SendAuditLog => Set<SendAuditEntry>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ContactNote> ContactNotes => Set<ContactNote>();
    public DbSet<ContactTag> ContactTags => Set<ContactTag>();
    public DbSet<ContactTagAssignment> ContactTagAssignments => Set<ContactTagAssignment>();
    public DbSet<ContactStageChange> ContactStageChanges => Set<ContactStageChange>();
    public DbSet<GroupLink> GroupLinks => Set<GroupLink>();
    public DbSet<User> Users => Set<User>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(MtrxDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
