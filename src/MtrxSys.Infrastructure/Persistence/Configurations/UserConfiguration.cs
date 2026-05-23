using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MtrxSys.Core.Domain.Users;

namespace MtrxSys.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Email).HasColumnName("email").IsRequired().HasMaxLength(200);
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired().HasMaxLength(120);
        b.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(120);
        b.Property(x => x.CreatedAt).HasColumnName("created_at").IsRequired();
        b.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
        b.Ignore(x => x.DomainEvents);
        b.Property<uint>("xmin").HasColumnName("xmin").IsConcurrencyToken().ValueGeneratedOnAddOrUpdate();
    }
}
