using System.Collections.Concurrent;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员详情进程缓存：同一演员在「解析身份」和「取头像」两个阶段只取一次，
/// 后续影片也直接复用。TTL 6 小时，条目上限 2000。
/// </summary>
public static class ActorInfoCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromHours(6);
    private static readonly ConcurrentDictionary<string, Entry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<ActorInfo> GetAsync(string provider, string id,
        CancellationToken cancellationToken)
    {
        var key = provider + ":" + id;
        if (Cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry.Value;

        var info = await ApiClient.GetActorInfoAsync(provider, id, cancellationToken).ConfigureAwait(false);
        if (Cache.Count > 2000)
            Cache.Clear();
        Cache[key] = new Entry(info, DateTime.UtcNow.Add(Ttl));
        return info;
    }

    private sealed record Entry(ActorInfo Value, DateTime ExpiresAt);
}
