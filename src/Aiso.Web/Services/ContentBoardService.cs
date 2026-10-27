using Aiso.Domain.Entities;
using Aiso.Domain.Enums;
using Aiso.Infrastructure.Content;
using Aiso.Infrastructure.Persistence;
using Aiso.Infrastructure.Recommendations;
using Aiso.Infrastructure.Scraping;
using Microsoft.EntityFrameworkCore;

namespace Aiso.Web.Services;

/// <summary>One content item as the board shows it.</summary>
public sealed record ContentCard(
    Guid Id,
    string Channel,
    TargetChannelKind ChannelKind,
    ContentKind Kind,
    ContentStatus Status,
    string Title,
    string Question,
    string LanguageCode,
    string Source,
    string? Rationale,
    string? Draft,
    string? ImagePrompt,
    string? Platform,
    string? LiveUrl,
    string? LiveNote,
    DateTime? LiveCheckedAtUtc,
    string? CitedBy,
    DateTime? CitedAtUtc,
    string? ApprovedBy,
    string? Error,
    DateTime UpdatedAtUtc);

/// <summary>
/// The people side of the content loop: propose ideas from the latest audits, queue drafts for the worker's writing
/// model, save edits, approve, and record the live URL once the owning team has published. AISO never publishes.
/// </summary>
public sealed class ContentBoardService(IDbContextFactory<AisoDbContext> contextFactory)
{
    public async Task<IReadOnlyList<ContentCard>> GetBoardAsync(CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        return await db.ContentItems.AsNoTracking()
            .OrderByDescending(c => c.UpdatedAtUtc)
            .Select(c => new ContentCard(c.Id, c.TargetProfile!.DisplayName, c.TargetProfile.ChannelKind, c.Kind, c.Status, c.Title, c.Question,
                c.LanguageCode, c.Source, c.Rationale, c.Draft, c.ImagePrompt, c.Platform, c.LiveUrl, c.LiveNote, c.LiveCheckedAtUtc,
                c.CitedBy, c.CitedAtUtc, c.ApprovedBy, c.Error, c.UpdatedAtUtc))
            .ToListAsync(ct);
    }

    /// <summary>Adds new ideas from each active channel's latest audit. Returns how many were added.</summary>
    public async Task<int> FindIdeasAsync(string? user, CancellationToken ct = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var targets = await db.TargetProfiles.AsNoTracking().Where(t => t.IsActive).OrderBy(t => t.Code)
            .Select(t => new
            {
                t.Id, t.DisplayName, t.ChannelKind, t.BaseUrl,
                Questions = t.TestPrompts.Where(p => p.IsActive).Select(p => new { p.PromptText, p.LanguageCode }).ToList(),
            })
            .ToListAsync(ct);

        var findings = new List<ChannelFindings>();
        foreach (var t in targets)
        {
            var latest = await db.LiveSearchAuditLogs.AsNoTracking()
                .Where(a => a.TargetProfileId == t.Id && a.Status != AuditStatus.Failed && a.MetricsJson != null)
                .OrderByDescending(a => a.CreatedAtUtc)
                .Select(a => new { a.Id, a.MetricsJson })
                .FirstOrDefaultAsync(ct);
            var answered = await db.LiveSearchAuditLogs.AsNoTracking()
                .Where(a => a.TargetProfileId == t.Id && a.ProbeResults.Any())
                .OrderByDescending(a => a.CreatedAtUtc).Select(a => (Guid?)a.Id).FirstOrDefaultAsync(ct);
            var probes = answered is null ? [] : await db.EngineProbeResults.AsNoTracking()
                .Where(r => r.AuditLogId == answered && r.ErrorMessage == null)
                .Select(r => new ProbeSnapshot(r.PromptId, r.PromptText, r.LanguageCode, r.EngineKey, r.EngineName, r.MentionsBrand, r.CitesOwnedDomain, new List<string>()))
                .ToListAsync(ct);

            findings.Add(new ChannelFindings(t.Id, t.DisplayName, t.ChannelKind, t.BaseUrl, AuditMetricsDocument.TryParse(latest?.MetricsJson), probes,
                t.Questions.Select(q => (q.PromptText, q.LanguageCode)).ToList()));
        }

        var existing = (await db.ContentItems.AsNoTracking().Select(c => new { c.TargetProfileId, c.Kind, c.Question }).ToListAsync(ct))
            .Select(c => ContentLoop.Key(c.TargetProfileId, c.Kind, c.Question)).ToHashSet(StringComparer.Ordinal);
        var ideas = ContentLoop.ProposeIdeas(findings, existing);
        foreach (var idea in ideas)
        {
            idea.UpdatedBy = user;
        }

        db.ContentItems.AddRange(ideas);
        await db.SaveChangesAsync(ct);
        return ideas.Count;
    }

    /// <summary>Queues an idea (or a draft to rewrite) for the writing model.</summary>
    public Task RequestDraftAsync(Guid id, string? user, CancellationToken ct = default) =>
        UpdateAsync(id, user, c =>
        {
            Require(c, ContentStatus.Idea, ContentStatus.InReview);
            c.Status = ContentStatus.Drafting;
            c.Error = null;
        }, ct);

    public Task SaveDraftAsync(Guid id, string draft, string? imagePrompt, string? user, CancellationToken ct = default) =>
        UpdateAsync(id, user, c =>
        {
            Require(c, ContentStatus.InReview, ContentStatus.Approved);
            c.Draft = draft;
            c.ImagePrompt = string.IsNullOrWhiteSpace(imagePrompt) ? null : imagePrompt.Trim();
        }, ct);

    public Task ApproveAsync(Guid id, string draft, string? imagePrompt, string? user, CancellationToken ct = default) =>
        UpdateAsync(id, user, c =>
        {
            Require(c, ContentStatus.InReview);
            c.Draft = draft;
            c.ImagePrompt = string.IsNullOrWhiteSpace(imagePrompt) ? null : imagePrompt.Trim();
            c.Status = ContentStatus.Approved;
            c.ApprovedBy = user;
        }, ct);

    /// <summary>Sends approved or published content back for edits; a saved live link is cleared.</summary>
    public Task ReopenAsync(Guid id, string? user, CancellationToken ct = default) =>
        UpdateAsync(id, user, c =>
        {
            Require(c, ContentStatus.Approved, ContentStatus.Published, ContentStatus.Live);
            c.Status = ContentStatus.InReview;
            c.ApprovedBy = null;
            c.LiveUrl = null;
            c.LiveNote = null;
            c.LiveCheckedAtUtc = null;
        }, ct);

    /// <summary>Records where the team published it; the worker then checks the address.</summary>
    public Task MarkPublishedAsync(Guid id, string url, string? user, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
        {
            throw new ArgumentException("Enter the full address of the published page, starting with https://.", nameof(url));
        }

        return UpdateAsync(id, user, c =>
        {
            Require(c, ContentStatus.Approved, ContentStatus.Published, ContentStatus.Live);
            c.LiveUrl = uri.ToString();
            c.Status = ContentStatus.Published;
            c.LiveCheckedAtUtc = null;
            c.LiveNote = null;
        }, ct);
    }

    public Task DismissAsync(Guid id, string? user, CancellationToken ct = default) =>
        UpdateAsync(id, user, c => c.Status = ContentStatus.Dismissed, ct);

    public Task RestoreAsync(Guid id, string? user, CancellationToken ct = default) =>
        UpdateAsync(id, user, c =>
        {
            Require(c, ContentStatus.Dismissed);
            c.Status = string.IsNullOrWhiteSpace(c.Draft) ? ContentStatus.Idea : ContentStatus.InReview;
        }, ct);

    private async Task UpdateAsync(Guid id, string? user, Action<ContentItem> change, CancellationToken ct)
    {
        await using var db = await contextFactory.CreateDbContextAsync(ct);
        var item = await db.ContentItems.FirstOrDefaultAsync(c => c.Id == id, ct)
                   ?? throw new InvalidOperationException("This item no longer exists.");
        change(item);
        item.UpdatedBy = user;
        await db.SaveChangesAsync(ct);
    }

    private static void Require(ContentItem item, params ContentStatus[] allowed)
    {
        if (!allowed.Contains(item.Status))
        {
            throw new InvalidOperationException($"This item is now “{item.Status}”; refresh the board.");
        }
    }
}
