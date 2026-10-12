using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class TargetProfileConfiguration : IEntityTypeConfiguration<TargetProfile>
{
    public void Configure(EntityTypeBuilder<TargetProfile> builder)
    {
        builder.ConfigureBase("AISO_TARGET_PROFILE");

        builder.Property(t => t.Code).HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(t => t.DisplayName).HasMaxLength(ColumnLengths.Name).IsRequired();
        builder.Property(t => t.BaseUrl).HasMaxLength(ColumnLengths.Url).IsRequired();
        builder.Property(t => t.BrandKeywords).HasMaxLength(ColumnLengths.ShortText).IsRequired();
        builder.Property(t => t.PrimaryLanguageCode).HasMaxLength(ColumnLengths.LanguageCode).IsRequired();

        builder.HasIndex(t => t.Code).IsUnique();

        builder.HasMany(t => t.TestPrompts).WithOne(p => p.TargetProfile)
            .HasForeignKey(p => p.TargetProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.AuditLogs).WithOne(a => a.TargetProfile)
            .HasForeignKey(a => a.TargetProfileId).OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(t => t.RemediationPlans).WithOne(r => r.TargetProfile)
            .HasForeignKey(r => r.TargetProfileId).OnDelete(DeleteBehavior.Cascade);
    }
}
