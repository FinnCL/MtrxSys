using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Conversations;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class ChatMessageConfiguration : IEntityTypeConfiguration<ChatMessage>
{
    public void Configure(EntityTypeBuilder<ChatMessage> b)
    {
        b.ToTable("chat_messages");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.ConversationId).HasColumnName("conversation_id").IsRequired();
        b.HasIndex(x => x.ConversationId);
        b.Property(x => x.WaMessageId).HasColumnName("wa_message_id").IsRequired().HasMaxLength(120);
        b.HasIndex(x => x.WaMessageId).IsUnique();
        b.Property(x => x.Direction).HasColumnName("direction").HasConversion<int>().IsRequired();
        b.Property(x => x.AuthorPhone).HasColumnName("author_phone").HasMaxLength(20);
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.Timestamp).HasColumnName("timestamp").IsRequired();
        b.HasIndex(x => new { x.ConversationId, x.Timestamp });
        b.Property(x => x.MediaUrl).HasColumnName("media_url").HasMaxLength(500);
        b.Ignore(x => x.DomainEvents);
        b.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(x => x.ConversationId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
