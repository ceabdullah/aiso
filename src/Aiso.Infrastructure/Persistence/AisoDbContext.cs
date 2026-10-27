using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Infrastructure.Persistence;

/// <summary>
/// Provider-agnostic EF Core context. Nothing in here references SQL Server or Oracle:
/// the provider is chosen at registration time (<see cref="AisoDatabaseExtensions"/>) and
/// each provider owns its own migrations assembly.
/// </summary>
public class AisoDbContext(DbContextOptions<AisoDbContext> options) : DbContext(options)
{
    public DbSet<TargetProfile> TargetProfiles => Set<TargetProfile>();
    public DbSet<AutomatedTestPrompt> AutomatedTestPrompts => Set<AutomatedTestPrompt>();
    public DbSet<LiveSearchAuditLog> LiveSearchAuditLogs => Set<LiveSearchAuditLog>();
    public DbSet<AisoRemediationPlan> AisoRemediationPlans => Set<AisoRemediationPlan>();
    public DbSet<GlobalSystemSetting> GlobalSystemSettings => Set<GlobalSystemSetting>();
    public DbSet<SecurityRoleMapping> SecurityRoleMappings => Set<SecurityRoleMapping>();
    public DbSet<EngineProbeResult> EngineProbeResults => Set<EngineProbeResult>();
    public DbSet<AnswerEngineSetting> AnswerEngineSettings => Set<AnswerEngineSetting>();
    public DbSet<ContentItem> ContentItems => Set<ContentItem>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        // Every DateTime is persisted as UTC and re-hydrated with Kind=Utc on every provider.
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();
        configurationBuilder.Properties<DateTime?>().HaveConversion<NullableUtcDateTimeConverter>();

        // Scores: fixed precision that both NUMBER(p,s) and DECIMAL(p,s) represent identically.
        configurationBuilder.Properties<decimal>().HavePrecision(5, 2);

        // Enums as bounded strings: readable in both databases and immune to reordering.
        configurationBuilder.Properties<TargetChannelKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<AuditStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<RemediationStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<AisoRole>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ContentKind>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<ContentStatus>().HaveConversion<string>().HaveMaxLength(32);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AisoDbContext).Assembly);
        ApplyLargeTextColumns(modelBuilder);

        // Must run last: rewrites every identifier to UPPER_SNAKE_CASE (<= 30 chars) so the schema
        // is idiomatic and legal on Oracle while remaining perfectly valid on SQL Server.
        PortableNamingConvention.Apply(modelBuilder);
    }

    private void ApplyLargeTextColumns(ModelBuilder modelBuilder)
    {
        // The only provider-aware line in the model: Oracle needs an explicit LOB type for unbounded text.
        var isOracle = Database.ProviderName?.Contains("Oracle", StringComparison.OrdinalIgnoreCase) == true;
        foreach (var property in modelBuilder.Model.GetEntityTypes().SelectMany(e => e.GetProperties()))
        {
            if (property.FindAnnotation(LargeTextExtensions.Annotation)?.Value is true)
            {
                property.SetMaxLength(null);
                if (isOracle)
                {
                    property.SetColumnType("NCLOB");
                }
            }
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAuditFields()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.CreatedAtUtc = now;
                    entry.Entity.UpdatedAtUtc = now;
                    break;
                case EntityState.Modified:
                    entry.Entity.UpdatedAtUtc = now;
                    entry.Entity.ConcurrencyStamp = Guid.NewGuid();
                    break;
            }
        }
    }
}
