using System.Globalization;

namespace Aiso.Infrastructure.Engines;

public static class BrandMatcher
{
    /// <summary>Case- and culture-aware keyword match (Turkish dotted/dotless I handled via the prompt's culture).</summary>
    public static List<string> MatchKeywords(string answer, IReadOnlyList<string> keywords, string languageCode)
    {
        ArgumentNullException.ThrowIfNull(answer);
        ArgumentNullException.ThrowIfNull(keywords);

        CultureInfo culture;
        try
        {
            culture = CultureInfo.GetCultureInfo(languageCode);
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
        }

        var compare = culture.CompareInfo;
        return keywords
            .Where(k => compare.IndexOf(answer, k, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0)
            .ToList();
    }
}
