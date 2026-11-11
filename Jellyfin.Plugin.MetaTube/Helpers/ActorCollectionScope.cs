namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员合集的媒体库范围（白名单）。空范围 = 全部媒体库（保持旧行为）；
/// 配置里的空值、无效 Guid 与重复项一律忽略。任务、按演员反查与增量监听共用这一份口径。
/// </summary>
public static class ActorCollectionScope
{
    /// <summary>
    /// 把配置里的库 id 字符串解析成 Guid 列表。空列表表示不做范围限制——
    /// 旧配置没有这个字段，升级后行为必须原样不变。
    /// </summary>
    public static Guid[] ParseLibraryIds(IEnumerable<string> raw)
    {
        if (raw is null)
            return Array.Empty<Guid>();

        var result = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var value in raw)
        {
            if (!Guid.TryParse(value, out var id) || !seen.Add(id))
                continue;

            // 保持配置顺序：查询与日志里的顺序稳定，重复运行不产生无意义的差异。
            result.Add(id);
        }

        return result.ToArray();
    }

    /// <summary>
    /// 影片是否在范围内。范围为空表示全部库都参与；
    /// 链接条目可能同时属于多个媒体库，任一所属库命中即视为在范围内。
    /// </summary>
    public static bool Includes(IReadOnlyCollection<Guid> scope, IEnumerable<Guid> libraryIds)
    {
        if (scope is null || scope.Count == 0)
            return true;

        var wanted = scope as ISet<Guid> ?? new HashSet<Guid>(scope);
        return libraryIds.Any(wanted.Contains);
    }
}
