using System.Security.Claims;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Engines;
using Aiso.Infrastructure.Notifications;
using Aiso.Web.Security;
using Aiso.Worker.Pipeline;

namespace Aiso.Tests;

public class AdGroupTests
{
    [Theory]
    [InlineData("CN=GG-AISO-Admins,OU=Groups,DC=corp,DC=local", "GG-AISO-Admins")]
    [InlineData(@"CORP\GG-AISO-Analysts", "GG-AISO-Analysts")]
    [InlineData("GG-AISO-Viewers@corp.local", "GG-AISO-Viewers")]
    [InlineData(@"CN=Risk\, Audit,OU=Groups,DC=corp", "Risk, Audit")]
    [InlineData("  GG-Plain  ", "GG-Plain")]
    [InlineData("", null)]
    public void ToCommonName_normalises_group_formats(string input, string? expected) =>
        Assert.Equal(expected, AdGroupNameParser.ToCommonName(input));

    [Fact]
    public void GetGroupCommonNames_reads_ldap_role_and_groups_claims()
    {
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.Role, "CN=GG-AISO-Admins,OU=G,DC=x"),
            new Claim("groups", @"CORP\GG-AISO-Analysts"),
        ], "test");

        var names = AisoClaimsTransformation.GetGroupCommonNames(new ClaimsPrincipal(identity));

        Assert.Contains("gg-aiso-admins", names); // case-insensitive set
        Assert.Contains("GG-AISO-Analysts", names);
    }
}

public class PromptProbeTests
{
    [Theory]
    [InlineData("Öneri: TEB ve Garanti öne çıkıyor.", "tr-TR", true)]
    [InlineData("türk ekonomi bankası güçlü bir seçenek", "tr-TR", true)]
    [InlineData("Consider Turk Ekonomi Bankasi for SMEs.", "en-US", true)]
    [InlineData("Akbank and Garanti BBVA lead the market.", "en-US", false)]
    public void MatchKeywords_is_case_and_culture_aware(string answer, string lang, bool expected)
    {
        var keywords = new[] { "TEB", "Türk Ekonomi Bankası", "Turk Ekonomi Bankasi" };
        Assert.Equal(expected, BrandMatcher.MatchKeywords(answer, keywords, lang).Count > 0);
    }
}

public class NotificationTests
{
    [Fact]
    public void BuildHtml_encodes_values_and_shows_delta()
    {
        var html = AisoNotificationService.BuildHtml(
            [new VisibilityAlertRow("<b>TEB</b>", "https://www.teb.com.tr", 41m, 70m, 22m, 55m, "reports/x_AISO_remediation_plan.md")],
            60, "https://aiso.corp.local", new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains("&lt;b&gt;TEB&lt;/b&gt;", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<b>TEB</b>", html, StringComparison.Ordinal);
        Assert.Contains("-14.0", html, StringComparison.Ordinal);
        Assert.Contains("Open dashboard", html, StringComparison.Ordinal);
    }
}

public class AgentPromptTests
{
    [Fact]
    public void Build_points_the_agent_at_the_run_folder_and_language()
    {
        var target = new TargetProfile { Code = "cepteteb", DisplayName = "CEPTETEB", BaseUrl = "https://www.cepteteb.com.tr", ChannelKind = TargetChannelKind.DigitalChannel };
        var prompt = AgentPromptBuilder.Build(target, "data/20261012-093000_abcd1234", "tr-TR");

        Assert.Contains("Analyze the scraped results in data/20261012-093000_abcd1234/", prompt, StringComparison.Ordinal);
        Assert.Contains("cepteteb.scrape.json", prompt, StringComparison.Ordinal);
        Assert.Contains("\"tr-TR\"", prompt, StringComparison.Ordinal);
        Assert.Contains("Do not create or modify any files", prompt, StringComparison.Ordinal);
    }
}
