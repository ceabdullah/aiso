using Aiso.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Aiso.Migrations.SqlServer;

/// <summary>
/// Used only by <c>dotnet ef</c>. Generating migrations does not connect to the database.
/// <code>dotnet ef migrations add &lt;Name&gt; --project src/Aiso.Migrations.SqlServer</code>
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AisoDbContext>
{
    public AisoDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__aisodb")
            ?? "Server=localhost,1433;Database=aisodb;Integrated Security=true;TrustServerCertificate=true";

        var builder = new DbContextOptionsBuilder<AisoDbContext>();
        AisoDatabaseExtensions.Configure(builder, new DatabaseOptions { Provider = DatabaseProvider.SqlServer }, connectionString);
        return new AisoDbContext(builder.Options);
    }
}
