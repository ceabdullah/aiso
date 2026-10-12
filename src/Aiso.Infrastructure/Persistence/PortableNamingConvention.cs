using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Aiso.Infrastructure.Persistence;

/// <summary>
/// Applies an UPPER_SNAKE_CASE naming scheme to tables, columns, keys, foreign keys and indexes,
/// capped at 30 characters (the most restrictive Oracle limit, pre-12.2). Overlong names are
/// truncated deterministically with a short hash suffix so that they stay unique and stable
/// across migrations.
/// </summary>
internal static class PortableNamingConvention
{
    public const int MaxIdentifierLength = 30;

    /// <summary>Tables are namespaced with this prefix; it is omitted from constraint/index names to save length.</summary>
    public const string TablePrefix = "AISO_";

    public static void Apply(ModelBuilder modelBuilder)
    {
        foreach (var entity in modelBuilder.Model.GetEntityTypes())
        {
            var table = entity.GetTableName();
            if (table is null)
            {
                continue;
            }

            // Tables configured explicitly keep their name; otherwise derive one.
            var tableName = Fit(table.Contains('_', StringComparison.Ordinal) ? table : ToUpperSnake(table));
            entity.SetTableName(tableName);
            var storeObject = StoreObjectIdentifier.Table(tableName, entity.GetSchema());
            var shortName = tableName.StartsWith(TablePrefix, StringComparison.Ordinal) ? tableName[TablePrefix.Length..] : tableName;

            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(Fit(ToUpperSnake(property.GetColumnName(storeObject) ?? property.Name)));
            }

            foreach (var key in entity.GetKeys())
            {
                key.SetName(Fit(key.IsPrimaryKey() ? $"PK_{tableName}" : $"AK_{shortName}_{JoinColumns(key.Properties, storeObject)}"));
            }

            foreach (var fk in entity.GetForeignKeys())
            {
                var principal = fk.PrincipalEntityType.GetTableName() ?? fk.PrincipalEntityType.ShortName();
                var principalShort = ToUpperSnake(principal).Replace(TablePrefix, string.Empty, StringComparison.Ordinal);
                fk.SetConstraintName(Fit($"FK_{shortName}_{principalShort}"));
            }

            foreach (var index in entity.GetIndexes())
            {
                index.SetDatabaseName(Fit($"IX_{shortName}_{JoinColumns(index.Properties, storeObject)}"));
            }
        }
    }

    private static string JoinColumns(IEnumerable<IMutableProperty> properties, StoreObjectIdentifier storeObject) =>
        string.Join('_', properties.Select(p => p.GetColumnName(storeObject) ?? ToUpperSnake(p.Name)));

    internal static string ToUpperSnake(string name)
    {
        var sb = new StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && name[i - 1] != '_' &&
                (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                sb.Append('_');
            }

            sb.Append(char.ToUpperInvariant(c));
        }

        return sb.ToString();
    }

    internal static string Fit(string identifier)
    {
        if (identifier.Length <= MaxIdentifierLength)
        {
            return identifier;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identifier)))[..6];
        return string.Create(CultureInfo.InvariantCulture, $"{identifier[..(MaxIdentifierLength - 7)]}_{hash}");
    }
}
