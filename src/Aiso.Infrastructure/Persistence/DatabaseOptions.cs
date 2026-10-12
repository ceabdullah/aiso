namespace Aiso.Infrastructure.Persistence;

public enum DatabaseProvider
{
    SqlServer = 0,
    Oracle = 1,
}

/// <summary>Bound from the <c>Aiso:Database</c> configuration section.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Aiso:Database";

    /// <summary>Name of the connection string (matches the Aspire resource name).</summary>
    public const string ConnectionStringName = "aisodb";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.SqlServer;

    /// <summary>Oracle only: database compatibility level (19 or 23). Controls BOOLEAN vs NUMBER(1), etc.</summary>
    public int OracleCompatibilityVersion { get; set; } = 19;

    public int CommandTimeoutSeconds { get; set; } = 60;

    public int MaxRetryCount { get; set; } = 5;
}

/// <summary>Assembly names that own each provider's migrations.</summary>
public static class MigrationAssemblies
{
    public const string SqlServer = "Aiso.Migrations.SqlServer";
    public const string Oracle = "Aiso.Migrations.Oracle";
}
