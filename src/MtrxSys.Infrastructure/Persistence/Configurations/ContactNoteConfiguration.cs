using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class ContactNoteConfiguration : IEntityTypeConfiguration<ContactNote>
{
    public void Configure(EntityTypeBuilder<ContactNote> b)
    {
        b.ToTable("contact_notes");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ContactId).HasColumnName("contact_id").IsRequired();
        b.HasIndex(x => x.ContactId);
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.CreatedByUserId).HasColumnName("created_by_user_id").IsRequired();
        b.Ignore(x => x.DomainEvents);
        b.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(x => x.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
