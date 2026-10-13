namespace Aiso.Infrastructure.Engines;

/// <summary>
/// The approximate place the answer engines search from, so results resemble what customers in that market see.
/// </summary>
/// <param name="Country">Two-letter ISO 3166-1 code, e.g. <c>TR</c>.</param>
/// <param name="Timezone">IANA time zone, e.g. <c>Europe/Istanbul</c>.</param>
public sealed record SearchLocation(string Country, string? Region, string? City, string? Timezone)
{
    /// <summary>Null when no country is set: the engines then search without a location.</summary>
    public static SearchLocation? Create(string? country, string? region, string? city, string? timezone) =>
        string.IsNullOrWhiteSpace(country) ? null
            : new SearchLocation(country.Trim().ToUpperInvariant(), Clean(region), Clean(city), Clean(timezone));

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
