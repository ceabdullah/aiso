using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Aiso.Infrastructure.Persistence;

/// <summary>Normalizes to UTC on write and stamps <see cref="DateTimeKind.Utc"/> on read.</summary>
internal sealed class UtcDateTimeConverter() : ValueConverter<DateTime, DateTime>(
    v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
    v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

internal sealed class NullableUtcDateTimeConverter() : ValueConverter<DateTime?, DateTime?>(
    v => v == null ? v : (v.Value.Kind == DateTimeKind.Utc ? v : v.Value.ToUniversalTime()),
    v => v == null ? v : DateTime.SpecifyKind(v.Value, DateTimeKind.Utc));
