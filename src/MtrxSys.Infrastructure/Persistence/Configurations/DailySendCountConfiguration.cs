using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class DailySendCountConfiguration : IEntityTypeConfiguration<DailySendCount>
{
    public void Configure(EntityTypeBuilder<DailySendCount> b)
    {
        b.ToTable("daily_send_counts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("date_utc");
        b.Property(x => x.SentCount).HasColumnName("sent_count").HasDefaultValue(0);
        b.Property(x => x.WarmupDayIndex).HasColumnName("warmup_day_index");
        b.Ignore(x => x.DomainEvents);
    }
}
