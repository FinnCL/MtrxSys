using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class SendAuditEntryConfiguration : IEntityTypeConfiguration<SendAuditEntry>
{
    public void Configure(EntityTypeBuilder<SendAuditEntry> b)
    {
        b.ToTable("send_audit_log");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.DispatchJobId).HasColumnName("dispatch_job_id");
        b.Property(x => x.PhoneE164).HasColumnName("phone_e164").HasMaxLength(20);
        b.Property(x => x.RenderedText).HasColumnName("rendered_text");
        b.Property(x => x.TypingMs).HasColumnName("typing_ms");
        b.Property(x => x.DelayMs).HasColumnName("delay_ms");
        b.Property(x => x.OccurredAt).HasColumnName("occurred_at");
        b.HasIndex(x => x.OccurredAt);
        b.Ignore(x => x.DomainEvents);
    }
}
