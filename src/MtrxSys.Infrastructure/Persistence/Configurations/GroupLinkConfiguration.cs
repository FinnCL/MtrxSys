using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class GroupLinkConfiguration : IEntityTypeConfiguration<GroupLink>
{
    public void Configure(EntityTypeBuilder<GroupLink> b)
    {
        b.ToTable("group_links");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.InviteCode).HasColumnName("invite_code").IsRequired().HasMaxLength(64);
        // Único: a deduplicação entre buscas é por código de convite.
        b.HasIndex(x => x.InviteCode).IsUnique();
        b.Property(x => x.InviteUrl).HasColumnName("invite_url").IsRequired().HasMaxLength(200);
        b.Property(x => x.SourceChannel).HasColumnName("source_channel").HasMaxLength(120);
        b.Property(x => x.GroupName).HasColumnName("group_name").HasMaxLength(300);
        b.Property(x => x.ParticipantCount).HasColumnName("participant_count");
        b.Property(x => x.Status).HasColumnName("status").HasConversion<int>().IsRequired();
        b.Property(x => x.MatchedKeyword).HasColumnName("matched_keyword").HasMaxLength(120);
        b.Property(x => x.WhatsAppGroupId).HasColumnName("whatsapp_group_id").HasMaxLength(120);
        b.Property(x => x.CollectedAt).HasColumnName("collected_at");
        b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        // Filtros quentes da tela do Coletor: por status e por palavra-chave de origem.
        b.HasIndex(x => x.Status);
        b.HasIndex(x => x.MatchedKeyword);
        b.Ignore(x => x.DomainEvents);
        b.Property<uint>("xmin").HasColumnName("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
    }
}
