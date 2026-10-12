namespace Aiso.Web.Security;

/// <summary>Normalises the many shapes an AD group arrives in down to its common name (CN).</summary>
public static class AdGroupNameParser
{
    /// <summary>
    /// <c>CN=GG-AISO-Admins,OU=Groups,DC=corp,DC=local</c> → <c>GG-AISO-Admins</c><br/>
    /// <c>CORP\GG-AISO-Admins</c> → <c>GG-AISO-Admins</c><br/>
    /// <c>GG-AISO-Admins@corp.local</c> → <c>GG-AISO-Admins</c>
    /// </summary>
    public static string? ToCommonName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        if (v.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            // Respect escaped commas inside the CN (RFC 4514), e.g. CN=Doe\, John,OU=...
            var end = 3;
            while (end < v.Length && !(v[end] == ',' && v[end - 1] != '\\'))
            {
                end++;
            }

            return v[3..end].Replace("\\,", ",", StringComparison.Ordinal).Trim();
        }

        var slash = v.LastIndexOf('\\');
        if (slash >= 0)
        {
            v = v[(slash + 1)..];
        }

        var at = v.IndexOf('@', StringComparison.Ordinal);
        return (at > 0 ? v[..at] : v).Trim();
    }
}
