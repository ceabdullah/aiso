using Aiso.Domain;
using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Aiso.Infrastructure.Persistence;

/// <summary>Idempotent reference-data seeding. Only inserts rows that are missing; never overwrites UI edits.</summary>
public sealed class DatabaseSeeder(ILogger<DatabaseSeeder> logger)
{
    public async Task SeedAsync(AisoDbContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await SeedSettingsAsync(context, cancellationToken);
        await SeedTargetsAsync(context, cancellationToken);
        await SeedRoleMappingsAsync(context, cancellationToken);

        var written = await context.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Seeder wrote {Count} row(s).", written);
    }

    private static async Task SeedSettingsAsync(AisoDbContext context, CancellationToken ct)
    {
        var existing = await context.GlobalSystemSettings.Select(s => s.Key).ToListAsync(ct);
        foreach (var (key, (value, description)) in SettingKeys.Defaults)
        {
            if (!existing.Contains(key))
            {
                context.GlobalSystemSettings.Add(new GlobalSystemSetting
                {
                    Key = key, Value = value, Description = description, UpdatedBy = "seed",
                });
            }
        }
    }

    private static async Task SeedTargetsAsync(AisoDbContext context, CancellationToken ct)
    {
        if (await context.TargetProfiles.AnyAsync(ct))
        {
            return;
        }

        var portal = new TargetProfile
        {
            Code = "teb-portal",
            DisplayName = "TEB Core Banking Portal",
            BaseUrl = "https://www.teb.com.tr",
            ChannelKind = TargetChannelKind.CoreWebPortal,
            BrandKeywords = "TEB,Türk Ekonomi Bankası,Turk Ekonomi Bankasi",
            PrimaryLanguageCode = "tr-TR",
        };
        var digital = new TargetProfile
        {
            Code = "cepteteb",
            DisplayName = "CEPTETEB Digital Channel",
            BaseUrl = "https://www.cepteteb.com.tr",
            ChannelKind = TargetChannelKind.DigitalChannel,
            BrandKeywords = "CEPTETEB,Cepteteb,TEB",
            PrimaryLanguageCode = "tr-TR",
        };
        var mobile = new TargetProfile
        {
            Code = "teb-ios",
            DisplayName = "TEB Mobile (App Store)",
            // Replace with the exact apps.apple.com listing URL for the bank's iOS app.
            BaseUrl = "https://apps.apple.com/tr/app/teb-mobil/id000000000",
            ChannelKind = TargetChannelKind.MobileAppStore,
            BrandKeywords = "TEB Mobil,CEPTETEB,TEB",
            PrimaryLanguageCode = "tr-TR",
        };

        AddPrompts(portal,
            ("tr-TR", "Türkiye'de KOBİ'ler için en iyi ticari kredi sunan bankalar hangileri?", "commercial-loans"),
            ("tr-TR", "Yatırım hesabı açmak için hangi bankayı önerirsin?", "investments"),
            ("en-US", "Which Turkish banks offer the best SME banking services?", "commercial-loans"));
        AddPrompts(digital,
            ("tr-TR", "Şubeye gitmeden tamamen dijital hesap açabileceğim bankalar hangileri?", "digital-onboarding"),
            ("tr-TR", "Vadeli mevduatta en yüksek faizi veren dijital bankalar hangileri?", "deposits"),
            ("en-US", "What are the best digital-only banking options in Turkey?", "digital-onboarding"));
        AddPrompts(mobile,
            ("tr-TR", "En kullanışlı mobil bankacılık uygulaması hangisi?", "mobile-banking"),
            ("en-US", "Which Turkish bank has the highest rated iPhone banking app?", "mobile-banking"));

        context.TargetProfiles.AddRange(portal, digital, mobile);
    }

    private static void AddPrompts(TargetProfile target, params (string Lang, string Text, string Category)[] prompts)
    {
        foreach (var (lang, text, category) in prompts)
        {
            target.TestPrompts.Add(new AutomatedTestPrompt { LanguageCode = lang, PromptText = text, Category = category });
        }
    }

    private static async Task SeedRoleMappingsAsync(AisoDbContext context, CancellationToken ct)
    {
        if (await context.SecurityRoleMappings.AnyAsync(ct))
        {
            return;
        }

        context.SecurityRoleMappings.AddRange(
            new SecurityRoleMapping { AdGroupCn = "GG-AISO-Administrators", Role = AisoRole.Administrator, Notes = "Placeholder — rename in the dashboard." },
            new SecurityRoleMapping { AdGroupCn = "GG-AISO-Analysts", Role = AisoRole.Analyst, Notes = "Placeholder — rename in the dashboard." },
            new SecurityRoleMapping { AdGroupCn = "GG-AISO-Viewers", Role = AisoRole.Viewer, Notes = "Placeholder — rename in the dashboard." });
    }
}
