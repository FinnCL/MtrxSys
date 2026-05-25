using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.SystemState;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class SystemStateConfiguration : IEntityTypeConfiguration<SystemStateAggregate>
{
    public void Configure(EntityTypeBuilder<SystemStateAggregate> b)
    {
        b.ToTable("system_state");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.OwnsOne(x => x.Circuit, c =>
        {
            c.Property(p => p.ConsecutiveFailures).HasColumnName("consecutive_failures").HasDefaultValue(0);
            c.Property(p => p.OpenUntil).HasColumnName("circuit_open_until");
        });
        b.Property(x => x.PausedReason).HasColumnName("paused_reason").HasMaxLength(500);
        b.Property(x => x.WarmupStartedOn).HasColumnName("warmup_started_on");
        b.Property(x => x.WarmupOverrideDate).HasColumnName("warmup_override_date");
        b.Property(x => x.WarmupBonusToday).HasColumnName("warmup_bonus_today").HasDefaultValue(0);
        b.Property(x => x.WarmupPhone).HasColumnName("warmup_phone").HasMaxLength(32);
        b.Ignore(x => x.DomainEvents);
        b.Property<uint>("xmin").HasColumnName("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
    }
}
