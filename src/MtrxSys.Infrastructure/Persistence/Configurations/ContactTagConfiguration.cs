using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class ContactTagConfiguration : IEntityTypeConfiguration<ContactTag>
{
    public void Configure(EntityTypeBuilder<ContactTag> b)
    {
        b.ToTable("contact_tags");
        b.HasKey(x => x.Name);
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(60);
        b.Property(x => x.Color).HasColumnName("color").HasMaxLength(20);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
    }
}

internal sealed class ContactTagAssignmentConfiguration : IEntityTypeConfiguration<ContactTagAssignment>
{
    public void Configure(EntityTypeBuilder<ContactTagAssignment> b)
    {
        b.ToTable("contact_tag_assignments");
        b.HasKey(x => new { x.ContactId, x.TagName });
        b.Property(x => x.ContactId).HasColumnName("contact_id");
        b.Property(x => x.TagName).HasColumnName("tag_name").IsRequired().HasMaxLength(60);
        b.Property(x => x.AssignedAt).HasColumnName("assigned_at").IsRequired();
        b.HasOne<Contact>()
            .WithMany()
            .HasForeignKey(x => x.ContactId)
            .OnDelete(DeleteBehavior.Cascade);
        b.HasOne<ContactTag>()
            .WithMany()
            .HasForeignKey(x => x.TagName)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
