using Aiso.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Aiso.Tests;

public class SchemaPortabilityTests
{
    private static AisoDbContext Create(DatabaseProvider provider)
    {
        var cs = provider == DatabaseProvider.Oracle
            ? "User Id=x;Password=x;Data Source=localhost:1521/FREEPDB1"
            : "Server=localhost;Database=x;TrustServerCertificate=true";
        var builder = new DbContextOptionsBuilder<AisoDbContext>();
        AisoDatabaseExtensions.Configure(builder, new DatabaseOptions { Provider = provider }, cs);
        return new AisoDbContext(builder.Options);
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.Oracle)]
    public void Migrations_are_in_sync_with_the_model(DatabaseProvider provider)
    {
        using var db = Create(provider);
        Assert.False(db.Database.HasPendingModelChanges(),
            $"{provider} migrations are out of date. Run: dotnet ef migrations add <Name> --project src/Aiso.Migrations.{provider}");
    }

    [Theory]
    [InlineData(DatabaseProvider.SqlServer)]
    [InlineData(DatabaseProvider.Oracle)]
    public void All_identifiers_fit_the_Oracle_30_character_limit(DatabaseProvider provider)
    {
        using var db = Create(provider);
        var model = db.GetService<IDesignTimeModel>().Model;

        var columns = new List<string>();
        var tablesAndIndexes = new List<string>(); // share one Oracle schema namespace
        var constraints = new List<string>();      // unique per schema
        foreach (var entity in model.GetEntityTypes())
        {
            var table = StoreObjectIdentifier.Table(entity.GetTableName()!, entity.GetSchema());
            tablesAndIndexes.Add(table.Name);
            tablesAndIndexes.AddRange(entity.GetIndexes().Select(i => i.GetDatabaseName()!));
            columns.AddRange(entity.GetProperties().Select(p => p.GetColumnName(table)!));
            constraints.AddRange(entity.GetKeys().Select(k => k.GetName()!));
            constraints.AddRange(entity.GetForeignKeys().Select(f => f.GetConstraintName()!));
        }

        var all = columns.Concat(tablesAndIndexes).Concat(constraints).ToList();
        Assert.All(all, n => Assert.True(n.Length <= PortableNamingConvention.MaxIdentifierLength, $"'{n}' is {n.Length} chars"));
        Assert.All(all, n => Assert.Equal(n.ToUpperInvariant(), n));
        Assert.Equal(tablesAndIndexes.Count, tablesAndIndexes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(constraints.Count, constraints.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Large_text_columns_map_to_NCLOB_on_Oracle_and_max_on_SqlServer()
    {
        using var oracle = Create(DatabaseProvider.Oracle);
        using var sql = Create(DatabaseProvider.SqlServer);

        string ColumnType(AisoDbContext db, string property)
        {
            var entity = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(Domain.Entities.AisoRemediationPlan))!;
            return entity.FindProperty(property)!.GetColumnType();
        }

        Assert.Equal("NCLOB", ColumnType(oracle, nameof(Domain.Entities.AisoRemediationPlan.MarkdownContent)));
        Assert.Equal("nvarchar(max)", ColumnType(sql, nameof(Domain.Entities.AisoRemediationPlan.MarkdownContent)));
    }

    [Theory]
    [InlineData("TargetProfileId", "TARGET_PROFILE_ID")]
    [InlineData("HttpStatusCode", "HTTP_STATUS_CODE")]
    [InlineData("AdGroupCn", "AD_GROUP_CN")]
    [InlineData("CreatedAtUtc", "CREATED_AT_UTC")]
    [InlineData("ID", "ID")]
    public void ToUpperSnake_converts_pascal_case(string input, string expected) =>
        Assert.Equal(expected, PortableNamingConvention.ToUpperSnake(input));
}
