using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class EngineProbeResultConfiguration : IEntityTypeConfiguration<EngineProbeResult>
{
    public void Configure(EntityTypeBuilder<EngineProbeResult> builder)
    {
        builder.ConfigureBase("AISO_ENGINE_PROBE");

        builder.Property(r => r.EngineKey).HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(r => r.EngineName).HasMaxLength(ColumnLengths.Code).IsRequired();
        builder.Property(r => r.LanguageCode).HasMaxLength(ColumnLengths.LanguageCode).IsRequired();
        builder.Property(r => r.PromptText).HasMaxLength(ColumnLengths.MediumText).IsRequired();
        builder.Property(r => r.MatchedKeywords).HasMaxLength(ColumnLengths.ShortText);
        builder.Property(r => r.CitationsJson).IsLargeText();
        builder.Property(r => r.AnswerExcerpt).HasMaxLength(ColumnLengths.MediumText);
        builder.Property(r => r.ErrorMessage).HasMaxLength(ColumnLengths.MediumText);
        builder.Ignore(r => r.IsVisible);

        // Results live and die with their audit log: the retention sweep deletes them via the cascade.
        // No FK to the target, which would create a second cascade path on SQL Server.
        builder.HasOne(r => r.AuditLog).WithMany(a => a.ProbeResults)
            .HasForeignKey(r => r.AuditLogId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(r => new { r.AuditLogId, r.EngineKey });
    }
}
