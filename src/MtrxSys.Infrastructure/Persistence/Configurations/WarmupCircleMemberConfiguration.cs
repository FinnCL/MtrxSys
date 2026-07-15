using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Warmup;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class WarmupCircleMemberConfiguration : IEntityTypeConfiguration<WarmupCircleMember>
{
    public void Configure(EntityTypeBuilder<WarmupCircleMember> b)
    {
        b.ToTable("warmup_circle");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.PhoneE164).HasColumnName("phone_e164").IsRequired().HasMaxLength(20);
        // Único: a mesma pessoa não entra no círculo duas vezes. SEM FK pra contacts de propósito —
        // esta gente NÃO é Contact (ver WarmupCircleMember).
        b.HasIndex(x => x.PhoneE164).IsUnique();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        b.Property(x => x.AddedAt).HasColumnName("added_at").IsRequired();
        b.Ignore(x => x.DomainEvents);
    }
}
