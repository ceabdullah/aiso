using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class ContentItemConfiguration : IEntityTypeConfiguration<ContentItem>
{
    public void Configure(EntityTypeBuilder<ContentItem> builder)
    {
        builder.ConfigureBase("AISO_CONTENT_ITEM");

        builder.Property(c => c.Title).HasMaxLength(ColumnLengths.ShortText).IsRequired();
        builder.Property(c => c.Question).HasMaxLength(ColumnLengths.MediumText).IsRequired();
        builder.Property(c => c.LanguageCode).HasMaxLength(ColumnLengths.LanguageCode).IsRequired();
        builder.Property(c => c.Source).HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(c => c.Rationale).HasMaxLength(ColumnLengths.MediumText);
        builder.Property(c => c.Draft).IsLargeText();
        builder.Property(c => c.ImagePrompt).HasMaxLength(ColumnLengths.MediumText);
        builder.Property(c => c.Platform).HasMaxLength(ColumnLengths.Code);
        builder.Property(c => c.ApprovedBy).HasMaxLength(ColumnLengths.Name);
        builder.Property(c => c.UpdatedBy).HasMaxLength(ColumnLengths.Name);
        builder.Property(c => c.LiveUrl).HasMaxLength(ColumnLengths.Url);
        builder.Property(c => c.LiveNote).HasMaxLength(ColumnLengths.ShortText);
        builder.Property(c => c.CitedBy).HasMaxLength(ColumnLengths.ShortText);
        builder.Property(c => c.Error).HasMaxLength(ColumnLengths.MediumText);

        builder.HasIndex(c => new { c.TargetProfileId, c.Status });
    }
}
