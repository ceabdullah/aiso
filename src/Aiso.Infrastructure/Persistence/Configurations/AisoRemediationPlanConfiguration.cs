using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class AisoRemediationPlanConfiguration : IEntityTypeConfiguration<AisoRemediationPlan>
{
    public void Configure(EntityTypeBuilder<AisoRemediationPlan> builder)
    {
        builder.ConfigureBase("AISO_REMEDIATION_PLAN");

        builder.Property(r => r.Title).HasMaxLength(ColumnLengths.ShortText).IsRequired();
        builder.Property(r => r.LanguageCode).HasMaxLength(ColumnLengths.LanguageCode).IsRequired();
        builder.Property(r => r.ReportFilePath).HasMaxLength(ColumnLengths.Url);
        builder.Property(r => r.AgentSessionId).HasMaxLength(ColumnLengths.Code);
        builder.Property(r => r.RawAgentOutput).IsLargeText();
        builder.Property(r => r.MarkdownContent).IsLargeText();

        // SetNull keeps plans when the originating audit log is purged by retention.
        // ClientSetNull avoids multiple-cascade-path errors on SQL Server.
        builder.HasOne(r => r.AuditLog).WithMany()
            .HasForeignKey(r => r.AuditLogId).OnDelete(DeleteBehavior.ClientSetNull);

        builder.HasIndex(r => new { r.TargetProfileId, r.CreatedAtUtc });
    }
}
