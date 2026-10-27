using System.Collections.Frozen;

namespace Aiso.Infrastructure.Reporting;

/// <summary>Static report chrome per language. Agent content itself is already produced in the target language.</summary>
internal sealed record ReportStrings(
    string Title,
    string Target,
    string Generated,
    string Language,
    string Scores,
    string Overall,
    string OnPage,
    string PromptVisibility,
    string Engines,
    string Engine,
    string Visibility,
    string Cited,
    string Summary,
    string Checklist,
    string Priority,
    string Category,
    string Owner,
    string NoItems,
    string Footer,
    IReadOnlyDictionary<RemediationPriority, string> PriorityNames);

internal static class ReportLocalization
{
    private static readonly FrozenDictionary<string, ReportStrings> Strings = new Dictionary<string, ReportStrings>(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = new(
            "AISO Remediation Plan", "Target", "Generated", "Language", "Visibility Scores", "Overall AISO score",
            "On-page structure", "AI answer visibility", "Visibility by AI engine", "Engine",
            "Brand visible", "Site cited", "Executive Summary", "Action Checklist", "Priority",
            "Category", "Owner", "The agent returned no actionable items.",
            "Generated automatically by the AISO platform. Review before acting; targets were audited as external black boxes.",
            new Dictionary<RemediationPriority, string>
            {
                [RemediationPriority.Critical] = "Critical", [RemediationPriority.High] = "High",
                [RemediationPriority.Medium] = "Medium", [RemediationPriority.Low] = "Low",
            }),
        ["tr"] = new(
            "AISO İyileştirme Planı", "Hedef", "Oluşturulma", "Dil", "Görünürlük Skorları", "Genel AISO skoru",
            "Sayfa içi yapı", "Yapay zekâ yanıt görünürlüğü", "Yapay zekâ motoruna göre görünürlük", "Motor",
            "Marka görünür", "Site kaynak gösterildi", "Yönetici Özeti", "Aksiyon Kontrol Listesi", "Öncelik",
            "Kategori", "Sorumlu", "Ajan uygulanabilir bir madde döndürmedi.",
            "AISO platformu tarafından otomatik oluşturulmuştur. Uygulamadan önce gözden geçiriniz; hedefler harici kara kutu olarak denetlenmiştir.",
            new Dictionary<RemediationPriority, string>
            {
                [RemediationPriority.Critical] = "Kritik", [RemediationPriority.High] = "Yüksek",
                [RemediationPriority.Medium] = "Orta", [RemediationPriority.Low] = "Düşük",
            }),
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static ReportStrings For(string languageCode)
    {
        var neutral = (languageCode ?? "en").Split('-', '_')[0];
        return Strings.TryGetValue(neutral, out var s) ? s : Strings["en"];
    }
}
