using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class SecurityRoleMappingConfiguration : IEntityTypeConfiguration<SecurityRoleMapping>
{
    public void Configure(EntityTypeBuilder<SecurityRoleMapping> builder)
    {
        builder.ConfigureBase("AISO_ROLE_MAPPING");

        builder.Property(m => m.AdGroupCn).HasMaxLength(ColumnLengths.Name).IsRequired();
        builder.Property(m => m.Role).HasColumnName("APP_ROLE"); // ROLE is an Oracle keyword
        builder.Property(m => m.Notes).HasMaxLength(ColumnLengths.ShortText);

        builder.HasIndex(m => new { m.AdGroupCn, m.Role }).IsUnique();
    }
}
