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
        b.Property(x => x.ImportedByPhone).HasColumnName("imported_by_phone").HasMaxLength(20);
        b.Property(x => x.Theme).HasColumnName("theme").HasMaxLength(80);
        b.Property(x => x.OptInAt).HasColumnName("opt_in_at");
        b.Property(x => x.OptOutAt).HasColumnName("opt_out_at");
        b.Property(x => x.LastSentAt).HasColumnName("last_sent_at");
        b.Property(x => x.Stage).HasColumnName("stage").HasConversion<int>().IsRequired();
        b.Property(x => x.StageChangedAt).HasColumnName("stage_changed_at");
        b.Property(x => x.DeletedAt).HasColumnName("deleted_at");
        b.Property(x => x.ReactivatedAt).HasColumnName("reactivated_at");
        b.HasIndex(x => x.Stage);
        // Índices dos filtros quentes: o público de disparo e a listagem por grupo varrem
        // contatos por group_tag e por opt_out (excluir quem saiu). Sem isso, full scan.
        // (deleted_at NÃO é indexado: o filtro é "IS NULL" e casa com quase todas as linhas,
        // então o índice seria ignorado pelo planner — só custaria escrita.)
        b.HasIndex(x => x.GroupTag);
        b.HasIndex(x => x.OptOutAt);
        b.Ignore(x => x.DomainEvents);
        b.Property<uint>("xmin").HasColumnName("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
    }
}
