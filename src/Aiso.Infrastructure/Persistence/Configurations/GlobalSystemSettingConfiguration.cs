using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class GlobalSystemSettingConfiguration : IEntityTypeConfiguration<GlobalSystemSetting>
{
    public void Configure(EntityTypeBuilder<GlobalSystemSetting> builder)
    {
        builder.ConfigureBase("AISO_SYSTEM_SETTING");

        builder.Property(s => s.Key).HasColumnName("SETTING_KEY").HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(s => s.Value).HasColumnName("SETTING_VALUE").HasMaxLength(ColumnLengths.MediumText).IsRequired();
        builder.Property(s => s.Description).HasMaxLength(ColumnLengths.ShortText);
        builder.Property(s => s.UpdatedBy).HasMaxLength(ColumnLengths.Name);

        builder.HasIndex(s => s.Key).IsUnique();
    }
}
