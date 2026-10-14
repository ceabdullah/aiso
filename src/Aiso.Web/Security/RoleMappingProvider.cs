using Aiso.Domain.Enums;
using Aiso.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Aiso.Web.Security;

/// <summary>Cached lookup of enabled AD-group → role mappings. Invalidated immediately when an admin saves changes.</summary>
public sealed class RoleMappingProvider(IDbContextFactory<AisoDbContext> contextFactory, IMemoryCache cache)
{
    private const string CacheKey = "aiso:role-mappings";

    public async Task<ILookup<string, AisoRole>> GetAsync(CancellationToken ct = default) =>
        (await cache.GetOrCreateAsync(CacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);
            await using var db = await contextFactory.CreateDbContextAsync(ct);
            var rows = await db.SecurityRoleMappings.AsNoTracking().Where(m => m.IsEnabled)
                .Select(m => new { m.AdGroupCn, m.Role }).ToListAsync(ct);
            return rows.ToLookup(r => r.AdGroupCn.Trim(), r => r.Role, StringComparer.OrdinalIgnoreCase);
        }))!;

    public void Invalidate() => cache.Remove(CacheKey);
}
