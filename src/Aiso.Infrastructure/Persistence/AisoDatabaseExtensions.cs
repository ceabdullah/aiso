using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Oracle.EntityFrameworkCore.Infrastructure;

namespace Aiso.Infrastructure.Persistence;

public static class AisoDatabaseExtensions
{
    /// <summary>
    /// Registers <see cref="AisoDbContext"/> against the provider named in <c>Aiso:Database:Provider</c>.
    /// Switching SQL Server -&gt; Oracle is a configuration change only (plus a connection string).
    /// </summary>
    public static IHostApplicationBuilder AddAisoDatabase(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var section = builder.Configuration.GetSection(DatabaseOptions.SectionName);
        builder.Services.Configure<DatabaseOptions>(section);
        var options = section.Get<DatabaseOptions>() ?? new DatabaseOptions();

        var connectionString = builder.Configuration.GetConnectionString(DatabaseOptions.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{DatabaseOptions.ConnectionStringName}' is not configured. " +
                "Under Aspire it is injected automatically; standalone, set ConnectionStrings__aisodb.");

        builder.Services.AddDbContextFactory<AisoDbContext>(db => Configure(db, options, connectionString));

        builder.Services.AddHealthChecks().AddDbContextCheck<AisoDbContext>("aiso-database", tags: ["ready"]);

        return builder;
    }

    public static DbContextOptionsBuilder Configure(DbContextOptionsBuilder db, DatabaseOptions options, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(options);

        switch (options.Provider)
        {
            case DatabaseProvider.SqlServer:
                db.UseSqlServer(connectionString, sql =>
                {
                    sql.MigrationsAssembly(MigrationAssemblies.SqlServer);
                    sql.MigrationsHistoryTable("AISO_EF_MIGRATIONS");
                    sql.CommandTimeout(options.CommandTimeoutSeconds);
                    sql.EnableRetryOnFailure(options.MaxRetryCount);
                });
                break;

            case DatabaseProvider.Oracle:
                db.UseOracle(connectionString, ora =>
                {
                    ora.MigrationsAssembly(MigrationAssemblies.Oracle);
                    ora.MigrationsHistoryTable("AISO_EF_MIGRATIONS");
                    ora.CommandTimeout(options.CommandTimeoutSeconds);
                    ora.UseOracleSQLCompatibility(options.OracleCompatibilityVersion >= 23
                        ? OracleSQLCompatibility.DatabaseVersion23
                        : OracleSQLCompatibility.DatabaseVersion19);
                });
                break;

            default:
                throw new NotSupportedException($"Database provider '{options.Provider}' is not supported.");
        }

        return db;
    }
}
