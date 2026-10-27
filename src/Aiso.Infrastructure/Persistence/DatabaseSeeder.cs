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
        await BackfillOwnedDomainsAsync(context, cancellationToken);
        await UpgradeTargetsAsync(context, cancellationToken);
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

    /// <summary>Domains whose citation by an AI engine counts as visibility. Editable in the database.</summary>
    private static readonly Dictionary<string, string> DefaultOwnedDomains = new(StringComparer.Ordinal)
    {
        ["teb-portal"] = "teb.com.tr",
        ["cepteteb"] = "cepteteb.com.tr",
        // TEB's retail iOS app is CEPTETEB; the bank's own sites also count for the app.
        ["teb-ios"] = "apps.apple.com/tr/app/cepteteb,teb.com.tr,cepteteb.com.tr",
        ["teb-brand"] = "teb.com.tr,cepteteb.com.tr,apps.apple.com/tr/app/cepteteb",
    };

    /// <summary>Databases created before answer-engine probing have no owned domains; fill the defaults once.</summary>
    private static async Task BackfillOwnedDomainsAsync(AisoDbContext context, CancellationToken ct)
    {
        var targets = await context.TargetProfiles.Where(t => t.OwnedDomains == null || t.OwnedDomains == string.Empty).ToListAsync(ct);
        foreach (var target in targets)
        {
            if (DefaultOwnedDomains.TryGetValue(target.Code, out var domains))
            {
                target.OwnedDomains = domains;
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
            OwnedDomains = DefaultOwnedDomains["teb-portal"],
            PrimaryLanguageCode = "tr-TR",
        };
        var digital = new TargetProfile
        {
            Code = "cepteteb",
            DisplayName = "CEPTETEB Digital Channel",
            BaseUrl = "https://www.cepteteb.com.tr",
            ChannelKind = TargetChannelKind.DigitalChannel,
            BrandKeywords = "CEPTETEB,Cepteteb,TEB",
            OwnedDomains = DefaultOwnedDomains["cepteteb"],
            PrimaryLanguageCode = "tr-TR",
        };
        var mobile = new TargetProfile
        {
            Code = "teb-ios",
            DisplayName = MobileName,
            BaseUrl = MobileUrl,
            ChannelKind = TargetChannelKind.MobileAppStore,
            BrandKeywords = "CEPTETEB,Cepteteb,TEB",
            OwnedDomains = DefaultOwnedDomains["teb-ios"],
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

        context.TargetProfiles.AddRange(portal, digital, mobile, BrandTarget());
    }

    private const string MobileName = "CEPTETEB (App Store)";
    private const string MobileUrl = "https://apps.apple.com/tr/app/cepteteb/id353385550";

    /// <summary>
    /// The bank as a brand: general questions about TEB that aren't tied to one website. The answers show how engines
    /// portray TEB, which banks they prefer and which sources shape that picture.
    /// </summary>
    private static TargetProfile BrandTarget()
    {
        var brand = new TargetProfile
        {
            Code = "teb-brand",
            DisplayName = "TEB (brand)",
            BaseUrl = "https://www.teb.com.tr",
            ChannelKind = TargetChannelKind.Brand,
            BrandKeywords = "TEB,Türk Ekonomi Bankası,Turk Ekonomi Bankasi,CEPTETEB",
            OwnedDomains = DefaultOwnedDomains["teb-brand"],
            PrimaryLanguageCode = "tr-TR",
        };
        AddPrompts(brand,
            ("tr-TR", "TEB (Türk Ekonomi Bankası) hakkında ne biliyorsun?", "brand"),
            ("tr-TR", "TEB güvenilir bir banka mı?", "brand-trust"),
            ("tr-TR", "TEB mi Garanti BBVA mı, hangisini önerirsin?", "brand-comparison"),
            ("tr-TR", "TEB müşterileri bankadan en çok neden şikayet ediyor?", "brand-reputation"),
            ("en-US", "What is TEB bank in Turkey known for?", "brand"),
            ("en-US", "Is TEB a good bank for expats living in Turkey?", "brand-trust"));
        return brand;
    }

    /// <summary>
    /// Brings databases seeded by earlier versions up to date: the placeholder App Store link becomes CEPTETEB's
    /// listing, and the brand channel is added. Idempotent; edits made since are left alone.
    /// </summary>
    private static async Task UpgradeTargetsAsync(AisoDbContext context, CancellationToken ct)
    {
        var targets = await context.TargetProfiles.Include(t => t.TestPrompts).ToListAsync(ct);
        if (targets.Count == 0)
        {
            return; // a new database gets everything from SeedTargetsAsync
        }

        if (targets.FirstOrDefault(t => t.Code == "teb-ios" && t.BaseUrl.Contains("id000000000", StringComparison.Ordinal)) is { } mobile)
        {
            mobile.BaseUrl = MobileUrl;
            mobile.DisplayName = MobileName;
            mobile.BrandKeywords = "CEPTETEB,Cepteteb,TEB";
            mobile.OwnedDomains = (mobile.OwnedDomains ?? string.Empty).Replace("apps.apple.com/tr/app/teb-mobil", "apps.apple.com/tr/app/cepteteb", StringComparison.Ordinal);
        }

        if (!targets.Any(t => t.Code == "teb-brand"))
        {
            context.TargetProfiles.Add(BrandTarget());
        }
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
