using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Contacts;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class ContactConfiguration : IEntityTypeConfiguration<Contact>
{
    public void Configure(EntityTypeBuilder<Contact> b)
    {
        b.ToTable("contacts");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.OwnsOne(x => x.Phone, p =>
        {
            p.Property(v => v.E164).HasColumnName("phone_e164").IsRequired().HasMaxLength(20);
            p.HasIndex(v => v.E164).IsUnique();
        });
        b.Navigation(x => x.Phone).IsRequired();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(200);
        b.Property(x => x.GroupTag).HasColumnName("group_tag").HasMaxLength(80);
        b.Property(x => x.Theme).HasColumnName("theme").HasMaxLength(80);
        b.Property(x => x.OptInAt).HasColumnName("opt_in_at");
        b.Property(x => x.OptOutAt).HasColumnName("opt_out_at");
        b.Property(x => x.LastSentAt).HasColumnName("last_sent_at");
        b.Property(x => x.Stage).HasColumnName("stage").HasConversion<int>().IsRequired();
        b.Property(x => x.StageChangedAt).HasColumnName("stage_changed_at");
        b.HasIndex(x => x.Stage);
        b.Ignore(x => x.DomainEvents);
        b.Property<uint>("xmin").HasColumnName("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
    }
}
