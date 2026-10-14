using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class AutomatedTestPromptConfiguration : IEntityTypeConfiguration<AutomatedTestPrompt>
{
    public void Configure(EntityTypeBuilder<AutomatedTestPrompt> builder)
    {
        builder.ConfigureBase("AISO_TEST_PROMPT");

        builder.Property(p => p.PromptText).HasMaxLength(ColumnLengths.MediumText).IsRequired();
        builder.Property(p => p.LanguageCode).HasMaxLength(ColumnLengths.LanguageCode).IsRequired();
        builder.Property(p => p.Category).HasMaxLength(ColumnLengths.Code);

        builder.HasIndex(p => new { p.TargetProfileId, p.LanguageCode });
    }
}
