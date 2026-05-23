using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    public void Configure(EntityTypeBuilder<Conversation> b)
    {
        b.ToTable("conversations");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WaChatId).HasColumnName("wa_chat_id").IsRequired().HasMaxLength(80);
        b.HasIndex(x => x.WaChatId).IsUnique();
        b.Property(x => x.ContactId).HasColumnName("contact_id");
        b.HasIndex(x => x.ContactId);
        b.Property(x => x.Title).HasColumnName("title").HasMaxLength(200);
        b.Property(x => x.IsGroup).HasColumnName("is_group").IsRequired();
        b.Property(x => x.LastMessageAt).HasColumnName("last_message_at");
        b.HasIndex(x => x.LastMessageAt);
        b.Property(x => x.LastMessagePreview).HasColumnName("last_message_preview").HasMaxLength(320);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Ignore(x => x.DomainEvents);
        b.Property<uint>("xmin").HasColumnName("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
    }
}
