using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

internal sealed class LiveSearchAuditLogConfiguration : IEntityTypeConfiguration<LiveSearchAuditLog>
{
    public void Configure(EntityTypeBuilder<LiveSearchAuditLog> builder)
    {
        builder.ConfigureBase("AISO_AUDIT_LOG");

        builder.Property(a => a.SnapshotPath).HasMaxLength(ColumnLengths.Url);
        builder.Property(a => a.ErrorMessage).HasMaxLength(ColumnLengths.MediumText);
        builder.Property(a => a.MetricsJson).IsLargeText();

        // Supports the retention sweep (ExecuteDelete by CreatedAtUtc) and the dashboard time series.
        builder.HasIndex(a => a.CreatedAtUtc);
        builder.HasIndex(a => new { a.TargetProfileId, a.CreatedAtUtc });
        builder.HasIndex(a => a.RunId);
    }
}
