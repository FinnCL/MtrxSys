using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Funnel;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class FunnelInviteConfiguration : IEntityTypeConfiguration<FunnelInvite>
{
    public void Configure(EntityTypeBuilder<FunnelInvite> b)
    {
        b.ToTable("funnel_invites");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ContactId).HasColumnName("contact_id").IsRequired();
        b.Property(x => x.PrefillText).HasColumnName("prefill_text").HasMaxLength(2000);
        b.Property(x => x.AutoReplyText).HasColumnName("auto_reply_text").HasMaxLength(2000);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.EngagedAt).HasColumnName("engaged_at");
        b.Property(x => x.AutoRepliedAt).HasColumnName("auto_replied_at");
        // O webhook busca o convite ABERTO do contato (engaged_at IS NULL) no 1º inbound → índice quente.
        b.HasIndex(x => x.ContactId);
        b.Ignore(x => x.DomainEvents);
    }
}
