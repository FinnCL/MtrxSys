using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class OwnedGroupConfiguration : IEntityTypeConfiguration<OwnedGroup>
{
    public void Configure(EntityTypeBuilder<OwnedGroup> b)
    {
        b.ToTable("owned_groups");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        // 40 cobre o id de grupo do WhatsApp com folga (18 dígitos hoje; o legado usa dígitos+'-').
        b.Property(x => x.WaGroupId).HasColumnName("wa_group_id").IsRequired().HasMaxLength(40);
        // Único: o mesmo grupo não é registrado duas vezes (o POST é idempotente por causa disto).
        b.HasIndex(x => x.WaGroupId).IsUnique();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Ignore(x => x.DomainEvents);
    }
}
