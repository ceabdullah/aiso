using Aiso.Infrastructure.Reporting;
using Aiso.Infrastructure.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Aiso.Tests;

public class MarkdownExporterTests
{
    private static ReportContext Ctx(string lang) => new("teb-portal", "TEB Portal", "https://www.teb.com.tr", lang,
        42.5m, 70m, 25m, new DateTime(2026, 10, 12, 9, 30, 0, DateTimeKind.Utc), "sess-1");

    private const string JsonContract = """
        {"summary":"Marka, üretken yanıtlarda nadiren anılıyor.",
         "items":[
          {"title":"FAQPage şeması ekleyin","detail":"Sık sorulan sorular için JSON-LD.","priority":"high","category":"structured-data","owner":"Web"},
          {"title":"Uygulama açıklamasını güncelleyin","detail":"Anahtar kelimeler.","priority":"low","category":"app-store","owner":"Mobile"},
          {"title":"Organization şemasını düzeltin","priority":"critical","category":"structured-data"}
         ]}
        """;

    [Fact]
    public void ParseAgentOutput_reads_the_json_contract()
    {
        var p = AisoMarkdownExporter.ParseAgentOutput(JsonContract);
        Assert.Equal(3, p.Items.Count);
        Assert.Equal(RemediationPriority.High, p.Items[0].Priority);
        Assert.Equal(RemediationPriority.Critical, p.Items[2].Priority);
        Assert.StartsWith("Marka", p.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseAgentOutput_accepts_fenced_json_with_surrounding_prose()
    {
        var p = AisoMarkdownExporter.ParseAgentOutput($"Here is the plan:\n```json\n{JsonContract}\n```\nDone.");
        Assert.Equal(3, p.Items.Count);
    }

    [Fact]
    public void ParseAgentOutput_falls_back_to_free_text_bullets()
    {
        const string raw = """
            The portal lacks answer-ready content.

            1. [HIGH] Add FAQPage schema: mark up the top 20 customer questions.
               Prioritise loans and cards.
            2. (Düşük) Improve alt text on hero images
            - **Publish an llms.txt** — summarise products for AI crawlers
            """;

        var p = AisoMarkdownExporter.ParseAgentOutput(raw);

        Assert.Equal("The portal lacks answer-ready content.", p.Summary);
        Assert.Equal(3, p.Items.Count);
        Assert.Equal("Add FAQPage schema", p.Items[0].Title);
        Assert.Equal(RemediationPriority.High, p.Items[0].Priority);
        Assert.Contains("Prioritise loans", p.Items[0].Detail, StringComparison.Ordinal);
        Assert.Equal(RemediationPriority.Low, p.Items[1].Priority);
        Assert.Equal("Publish an llms.txt", p.Items[2].Title);
    }

    [Fact]
    public void Render_localises_chrome_to_turkish_and_orders_by_priority()
    {
        var md = AisoMarkdownExporter.Render(Ctx("tr-TR"), AisoMarkdownExporter.ParseAgentOutput(JsonContract));

        Assert.Contains("# AISO İyileştirme Planı — TEB Portal", md, StringComparison.Ordinal);
        Assert.Contains("## Aksiyon Kontrol Listesi", md, StringComparison.Ordinal);
        Assert.Contains("- [ ] **FAQPage şeması ekleyin**", md, StringComparison.Ordinal);
        Assert.True(md.IndexOf("Kritik", StringComparison.Ordinal) < md.IndexOf("Yüksek", StringComparison.Ordinal));
        Assert.True(md.IndexOf("Yüksek", StringComparison.Ordinal) < md.IndexOf("Düşük", StringComparison.Ordinal));
        Assert.Contains("| Genel AISO skoru | 42.5 |", md, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_falls_back_to_english_and_escapes_markup()
    {
        var payload = new RemediationPayload(null, [new RemediationItem("Fix <script>alert(1)</script> | table", null, "medium", null, null)]);
        var md = AisoMarkdownExporter.Render(Ctx("ja-JP"), payload);

        Assert.Contains("## Action Checklist", md, StringComparison.Ordinal);
        Assert.Contains("&lt;script&gt;", md, StringComparison.Ordinal);
        Assert.Contains("\\| table", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportAsync_writes_reports_file_matching_the_naming_contract()
    {
        var root = Directory.CreateTempSubdirectory("aiso-ws-").FullName;
        var exporter = new AisoMarkdownExporter(Options.Create(new WorkspaceOptions { RootPath = root }), NullLogger<AisoMarkdownExporter>.Instance);

        var report = await exporter.ExportAsync(Ctx("tr-TR"), JsonContract, TestContext.Current.CancellationToken);

        Assert.Equal("reports/teb-portal-tr-TR-20261012-0930_AISO_remediation_plan.md", report.RelativePath);
        Assert.True(File.Exists(report.AbsolutePath));
        Assert.Equal(3, report.ItemCount);
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "reports"), "*.tmp"));
    }
}
