using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Campaigns;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class DispatchJobConfiguration : IEntityTypeConfiguration<DispatchJob>
{
    public void Configure(EntityTypeBuilder<DispatchJob> b)
    {
        b.ToTable("dispatch_jobs");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ContactId).HasColumnName("contact_id");
        b.Property(x => x.TemplateId).HasColumnName("template_id");
        b.HasIndex(x => x.TemplateId);
        b.Property(x => x.Status).HasColumnName("status").HasConversion<int>();
        b.Property(x => x.ScheduledAt).HasColumnName("scheduled_at");
        b.Property(x => x.SentAt).HasColumnName("sent_at");
        b.Property(x => x.WahaMessageId).HasColumnName("waha_message_id").HasMaxLength(120);
        b.Property(x => x.ErrorReason).HasColumnName("error_reason").HasMaxLength(500);
        b.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasDefaultValue(0);
        // Adiamentos (≠ tentativas de envio). Sem o HasColumnName explícito o EF geraria "DeferCount"
        // em PascalCase no meio de um schema snake_case, e toda consulta psql à mão precisaria de aspas.
        b.Property(x => x.DeferCount).HasColumnName("defer_count").HasDefaultValue(0);
        b.HasIndex(x => new { x.Status, x.ScheduledAt });
        b.Ignore(x => x.DomainEvents);
        b.Property<uint>("xmin").HasColumnName("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
    }
}
