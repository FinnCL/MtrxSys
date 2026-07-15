using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Groups;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class OwnedGroupConfiguration : IEntityTypeConfiguration<OwnedGroup>
{
    public void Configure(EntityTypeBuilder<OwnedGroup> b)
    {
        b.ToTable("owned_groups");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        // 40 cobre o id de grupo do WhatsApp com folga (18 dígitos hoje; o legado usa dígitos+'-').
        b.Property(x => x.WaGroupId).HasColumnName("wa_group_id").IsRequired().HasMaxLength(40);
        // Único: o mesmo grupo não é registrado duas vezes (o POST é idempotente por causa disto).
        b.HasIndex(x => x.WaGroupId).IsUnique();
        b.Property(x => x.Name).HasColumnName("name").HasMaxLength(120);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.DispatchExemptionEnabled)
            .HasColumnName("dispatch_exemption_enabled").IsRequired().HasDefaultValue(false);
        // Coleção via campo: OwnedGroup expõe Members só pra leitura (a lista só muda pelos métodos
        // do agregado, que ligam a chave e a fotografia juntas).
        b.HasMany(x => x.Members)
            .WithOne()
            .HasForeignKey(m => m.OwnedGroupId)
            .OnDelete(DeleteBehavior.Cascade); // esquecer o grupo esquece a isenção junto
        b.Navigation(x => x.Members).HasField("members").UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Ignore(x => x.DomainEvents);
    }
}

internal sealed class OwnedGroupMemberConfiguration : IEntityTypeConfiguration<OwnedGroupMember>
{
    public void Configure(EntityTypeBuilder<OwnedGroupMember> b)
    {
        b.ToTable("owned_group_members");
        b.HasKey(x => x.Id);
        // ValueGeneratedNever é OBRIGATÓRIO aqui, não estilo. Estes membros entram pela COLEÇÃO de um
        // OwnedGroup já rastreado (nunca por um DbSet.Add), e já chegam com Id preenchido. Por
        // convenção o EF trata chave Guid como gerada pelo banco — então, ao encontrar um filho novo
        // com a chave PREENCHIDA, ele conclui "já existe" e marca Modified: emite UPDATE em linha
        // inexistente, 0 linhas afetadas, DbUpdateConcurrencyException, 500 na cara do operador.
        // Dizer que a chave é nossa faz o EF marcar Added e emitir INSERT.
        b.Property(x => x.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(x => x.OwnedGroupId).HasColumnName("owned_group_id").IsRequired();
        b.Property(x => x.PhoneE164).HasColumnName("phone_e164").IsRequired().HasMaxLength(20);
        // A pergunta do disparo é sempre "este telefone está isento?" — o índice é por telefone.
        b.HasIndex(x => x.PhoneE164);
        b.Ignore(x => x.DomainEvents);
    }
}
