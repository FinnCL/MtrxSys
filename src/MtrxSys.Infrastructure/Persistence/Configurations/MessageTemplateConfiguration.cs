using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Messages;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> b)
    {
        b.ToTable("message_templates");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Slot).HasColumnName("slot").HasConversion<int>();
        b.Property(x => x.ContentSpintax).HasColumnName("content_spintax").IsRequired();
        b.Property(x => x.Active).HasColumnName("active").HasDefaultValue(true);
        b.Property(x => x.ImageData).HasColumnName("image_data").HasColumnType("bytea");
        b.Property(x => x.ImageMimeType).HasColumnName("image_mime_type");
        b.HasIndex(x => new { x.Slot, x.Active });
        b.Ignore(x => x.DomainEvents);
    }
}
