using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class AnswerEngineSettingConfiguration : IEntityTypeConfiguration<AnswerEngineSetting>
{
    public void Configure(EntityTypeBuilder<AnswerEngineSetting> builder)
    {
        builder.ConfigureBase("AISO_ANSWER_ENGINE");

        builder.Property(e => e.EngineKey).HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(e => e.DisplayName).HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(e => e.Kind).HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(e => e.DefaultModel).HasMaxLength(ColumnLengths.Name);
        builder.Property(e => e.ModelOverride).HasMaxLength(ColumnLengths.Name);
        builder.Property(e => e.UpdatedBy).HasMaxLength(ColumnLengths.Name);

        builder.HasIndex(e => e.EngineKey).IsUnique();
    }
}
