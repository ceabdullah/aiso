using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Aiso.Infrastructure.AppStore;

public sealed record AppListing(
    long AppId,
    string Name,
    string? Seller,
    decimal? Rating,
    long? RatingCount,
    string? Version,
    DateTime? LastUpdatedUtc,
    int DescriptionLength,
    string? ReleaseNotes,
    int ScreenshotCount,
    string? Url);

public sealed record AppReview(string Title, string Content, int Rating, string? Version, DateTime? UpdatedUtc);

/// <summary>Public App Store data for a channel: its listing, recent reviews and leading peer apps for comparison.</summary>
public sealed record AppStoreData(AppListing? App, string? Error, IReadOnlyList<AppReview> RecentReviews, IReadOnlyList<AppListing> Peers)
{
    public decimal? RecentAverage => RecentReviews.Count == 0 ? null : Math.Round((decimal)RecentReviews.Average(r => r.Rating), 2);
}

public sealed class AppStoreOptions
{
    public const string SectionName = "Aiso:AppStore";

    /// <summary>Storefront country for lookups and reviews.</summary>
    public string Country { get; set; } = "tr";

    /// <summary>Search term for peer apps to compare with; empty skips the comparison.</summary>
    public string PeerSearchTerm { get; set; } = "mobil bankacılık";

    public int PeerCount { get; set; } = 6;
}

/// <summary>
/// Reads Apple's public iTunes Lookup/Search APIs and customer-reviews feed (no key, no scraping). Google Play has no
/// official public API, so Android listings are not read.
/// </summary>
public sealed partial class AppStoreClient(IHttpClientFactory httpClientFactory, IOptions<AppStoreOptions> options, ILogger<AppStoreClient> logger)
{
    public const string HttpClientName = "aiso-appstore";

    /// <summary>The numeric app id in an App Store URL (…/id353385550), or null.</summary>
    public static long? AppIdFrom(string url) =>
        AppIdRegex().Match(url ?? string.Empty) is { Success: true } m && long.TryParse(m.Groups[1].Value, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : null;

    public async Task<AppStoreData> GetAsync(string appStoreUrl, CancellationToken ct)
    {
        if (AppIdFrom(appStoreUrl) is not { } id)
        {
            return new AppStoreData(null, "The channel URL has no App Store app id (…/id<number>).", [], []);
        }

        var o = options.Value;
        try
        {
            using var lookup = await GetJsonAsync($"https://itunes.apple.com/lookup?id={id}&country={o.Country}", ct);
            var app = lookup is null ? null : ParseListings(lookup).FirstOrDefault();
            if (app is null)
            {
                return new AppStoreData(null, $"No App Store app with id {id} in the '{o.Country}' storefront. Check the channel URL.", [], []);
            }

            using var feed = await GetJsonAsync($"https://itunes.apple.com/{o.Country}/rss/customerreviews/id={id}/sortby=mostrecent/json", ct);
            var reviews = feed is null ? [] : ParseReviews(feed);

            List<AppListing> peers = [];
            if (!string.IsNullOrWhiteSpace(o.PeerSearchTerm))
            {
                using var search = await GetJsonAsync(
                    $"https://itunes.apple.com/search?term={Uri.EscapeDataString(o.PeerSearchTerm)}&country={o.Country}&entity=software&limit=25", ct);
                peers = (search is null ? [] : ParseListings(search))
                    .Where(p => p.AppId != id && !string.Equals(p.Seller, app.Seller, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(p => p.RatingCount ?? 0)
                    .Take(o.PeerCount)
                    .ToList();
            }

            return new AppStoreData(app, null, reviews, peers);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "App Store lookup failed for {Url}.", appStoreUrl);
            return new AppStoreData(null, $"App Store lookup failed: {ex.Message}", [], []);
        }
    }

    private async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        var http = httpClientFactory.CreateClient(HttpClientName);
        using var response = await http.GetAsync(new Uri(url), ct);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)) : null;
    }

    /// <summary>Listings from a Lookup or Search response. Public for unit tests.</summary>
    public static List<AppListing> ParseListings(JsonDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return results.EnumerateArray()
            .Where(r => r.TryGetProperty("trackId", out _))
            .Select(r => new AppListing(
                r.GetProperty("trackId").GetInt64(),
                Str(r, "trackName") ?? string.Empty,
                Str(r, "sellerName"),
                r.TryGetProperty("averageUserRating", out var rating) && rating.TryGetDecimal(out var d) ? Math.Round(d, 2) : null,
                r.TryGetProperty("userRatingCount", out var count) && count.TryGetInt64(out var c) ? c : null,
                Str(r, "version"),
                Date(Str(r, "currentVersionReleaseDate")),
                Str(r, "description")?.Length ?? 0,
                Str(r, "releaseNotes"),
                r.TryGetProperty("screenshotUrls", out var shots) && shots.ValueKind == JsonValueKind.Array ? shots.GetArrayLength() : 0,
                Str(r, "trackViewUrl")?.Split('?')[0]))
            .ToList();
    }

    /// <summary>Reviews from the customer-reviews JSON feed. Public for unit tests.</summary>
    public static List<AppReview> ParseReviews(JsonDocument doc)
    {
        ArgumentNullException.ThrowIfNull(doc);
        if (!doc.RootElement.TryGetProperty("feed", out var feed) || !feed.TryGetProperty("entry", out var entries))
        {
            return [];
        }

        // A single review comes back as an object, several as an array.
        var list = entries.ValueKind == JsonValueKind.Array ? entries.EnumerateArray().ToList() : [entries];
        return list
            .Where(e => e.TryGetProperty("im:rating", out _))
            .Select(e => new AppReview(
                Label(e, "title") ?? string.Empty,
                Label(e, "content") ?? string.Empty,
                int.TryParse(Label(e, "im:rating"), CultureInfo.InvariantCulture, out var r) ? r : 0,
                Label(e, "im:version"),
                Date(Label(e, "updated"))))
            .ToList();
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Label(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object && v.TryGetProperty("label", out var l) ? l.GetString() : null;

    private static DateTime? Date(string? value) =>
        DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var d) ? d.UtcDateTime : null;

    [GeneratedRegex(@"/id(\d+)")]
    private static partial Regex AppIdRegex();
}
