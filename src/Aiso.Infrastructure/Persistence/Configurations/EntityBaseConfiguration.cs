using Aiso.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Aiso.Infrastructure.Persistence.Configurations;

/// <summary>Shared mapping for <see cref="EntityBase"/> columns.</summary>
internal static class EntityBaseConfiguration
{
    public static void ConfigureBase<T>(this EntityTypeBuilder<T> builder, string tableName) where T : EntityBase
    {
        builder.ToTable(tableName);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.CreatedAtUtc).IsRequired();
        builder.Property(e => e.UpdatedAtUtc).IsRequired();
        builder.Property(e => e.ConcurrencyStamp).IsConcurrencyToken();
    }
}

/// <summary>
/// Length budget for text columns. Anything indexed or compared must be bounded (<= 2000 chars
/// keeps it inside Oracle NVARCHAR2 limits); unbounded properties become NVARCHAR(MAX) / NCLOB.
/// </summary>
internal static class ColumnLengths
{
    public const int Code = 64;
    public const int Name = 256;
    public const int Url = 1024;
    public const int LanguageCode = 16;
    public const int ShortText = 512;
    public const int MediumText = 2000;
}

internal static class LargeTextExtensions
{
    public const string Annotation = "Aiso:LargeText";

    /// <summary>
    /// Marks a string column as unbounded text. Resolved per provider in <see cref="AisoDbContext"/>:
    /// SQL Server <c>nvarchar(max)</c>, Oracle <c>NCLOB</c> (Oracle would otherwise default to NVARCHAR2(2000)).
    /// </summary>
    public static PropertyBuilder<string?> IsLargeText(this PropertyBuilder<string?> builder) =>
        builder.HasAnnotation(Annotation, true);
}
