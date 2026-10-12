using Aiso.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aiso.Migrations.Oracle;

/// <summary>
/// Used only by <c>dotnet ef</c>. Generating migrations does not connect to the database.
/// <code>dotnet ef migrations add &lt;Name&gt; --project src/Aiso.Migrations.Oracle</code>
/// Keep both providers' migrations in lock-step: every model change gets a migration in each project.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AisoDbContext>
{
    public AisoDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__aisodb")
            ?? "User Id=AISO;Password=change_me;Data Source=localhost:1521/FREEPDB1";

        var builder = new DbContextOptionsBuilder<AisoDbContext>();
        AisoDatabaseExtensions.Configure(builder, new DatabaseOptions { Provider = DatabaseProvider.Oracle }, connectionString);
        return new AisoDbContext(builder.Options);
    }
}
