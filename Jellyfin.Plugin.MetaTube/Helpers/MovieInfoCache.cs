using System.Collections.Concurrent;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 影片详情进程缓存：元数据刷新与图片阶段会取同一部影片的详情，这里只请求一次。
/// TTL 5 分钟，条目上限 1000；聚合结果仍走服务端自己的缓存。
/// </summary>
public static class MovieInfoCache
{
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(5);
    private static readonly ConcurrentDictionary<string, Entry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    public static async Task<Metadata.MovieInfo> GetAsync(string provider, string id,
        CancellationToken cancellationToken)
    {
        var key = provider + ":" + id;
        if (Cache.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.UtcNow)
            return entry.Value;

        var info = await ApiClient.GetMovieInfoAsync(provider, id, cancellationToken).ConfigureAwait(false);
        if (Cache.Count > 1000)
            Cache.Clear();
        Cache[key] = new Entry(info, DateTime.UtcNow.Add(Ttl));
        return info;
    }

    private sealed record Entry(Metadata.MovieInfo Value, DateTime ExpiresAt);
}
