using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class ContactStageChangeConfiguration : IEntityTypeConfiguration<ContactStageChange>
{
    public void Configure(EntityTypeBuilder<ContactStageChange> b)
    {
        b.ToTable("contact_stage_changes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ContactId).HasColumnName("contact_id").IsRequired();
        b.HasIndex(x => x.ContactId);
        b.Property(x => x.FromStage).HasColumnName("from_stage").HasConversion<int?>();
        b.Property(x => x.ToStage).HasColumnName("to_stage").HasConversion<int>().IsRequired();
        b.Property(x => x.ChangedAt).HasColumnName("changed_at").IsRequired();
        b.Property(x => x.ChangedByUserId).HasColumnName("changed_by_user_id").IsRequired();
        b.Ignore(x => x.DomainEvents);
        b.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(x => x.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
