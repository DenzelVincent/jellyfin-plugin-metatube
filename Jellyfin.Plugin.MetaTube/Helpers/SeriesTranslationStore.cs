using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 系列名译文表：整键查表 + 引擎新译结果回写持久化（设计 D14 / 4.6）。
/// 合集靠 CollectionName 按名字匹配，同一系列两次刮削拿到不同译文就会在库里裂成两个合集，
/// 所以缓存必须以「持久化后的 raw 串」为准，内存缓存挡不住重启后引擎换译法。
/// 只允许整键匹配：SubstitutionTable.Substitute 是子串批量替换，系列名互相包含时会误伤。
/// </summary>
public static class SeriesTranslationStore
{
    private static readonly object Sync = new();

    // SubstitutionTable 的构造函数是私有的，只能走 Parse 建实例。
    private static SubstitutionTable _table = SubstitutionTable.Parse(string.Empty);

    private static string _raw = string.Empty;

    /// <summary>纯查表，便于单测。命中即返回 true；表值为空串或 null 表示「保留原文」的显式意图。</summary>
    public static bool Lookup(string raw, string source, out string target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(raw) || string.IsNullOrWhiteSpace(source))
            return false;

        // 键要先裁再查：表里的键是 Parse 裁过的（`SubstitutionTable.cs:18`），
        // 拿没裁的原文去查，带空白的系列名永远 miss。
        return SubstitutionTable.Parse(raw).TryGetValue(source.Trim(), out target);
    }

    /// <summary>纯追加/替换一行，幂等：键值都相同时原样返回入参。含换行的键或值直接拒绝入表。</summary>
    public static string Append(string raw, string source, string target)
    {
        source = source?.Trim();
        target = target?.Trim();

        // 表是按行存的：Parse 逐行读、ToString 用 \n 拼。键或值里带换行就会写出多余的行，
        // 那行会被下次 Parse 读成「没有等号」的键——等于凭空多出一条「保留原文」的记录，只能手改 config.xml 才清得掉。
        if (string.IsNullOrEmpty(source) || source.Contains('\n') || source.Contains('\r') ||
            target != null && (target.Contains('\n') || target.Contains('\r')))
            return raw ?? string.Empty;

        var table = SubstitutionTable.Parse(raw);
        // 表值用 OrdinalIgnoreCase，与 SubstitutionTable 的键比较器同口径：键按大小写不敏感命中、
        // 值却按敏感比较，引擎这次给 "Abp" 下次给 "ABP" 就会来回改写同一行，合集名跟着裂（D14 要堵的正是这个）。
        if (table.TryGetValue(source, out var existing) && string.Equals(existing, target, StringComparison.OrdinalIgnoreCase))
            return raw ?? string.Empty;

        table[source] = target;
        return table.ToString();
    }

    /// <summary>
    /// 读当前配置里的译文表。缓存重建靠「当前 raw 串 ≠ 建表时的 raw 串」判断，
    /// 在锁内完成，这样一个缓存同时覆盖用户在配置页的编辑与刮削期的自动回写，
    /// 不必每张影片重解析文本。
    /// </summary>
    public static bool TryGetValue(string source, out string target)
    {
        target = null;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        lock (Sync)
        {
            var raw = Plugin.Instance.Configuration.RawSeriesTranslationTable ?? string.Empty;
            if (raw != _raw)
            {
                _table = SubstitutionTable.Parse(raw);
                _raw = raw;
            }

            return _table.TryGetValue(source.Trim(), out target);
        }
    }

#if !__EMBY__
    /// <summary>
    /// 把引擎新译的结果写回配置，下一轮刮削直接命中、不再调用引擎。
    /// 返回值是「真正生效的译文」：入库名必须与表里那行一致，否则同系列的多部影片会裂成两个合集（设计 D14）。
    /// 刮削是并发的，「读当前 raw → 判断 → 追加一行 → 整体写回」必须在同一个锁里做完（设计 V8）。
    /// Emby 端不做自动回写：BasePluginSimpleUI 怎么从刮削线程持久化配置没验证过（设计 4.4）。
    /// </summary>
    public static string Persist(string source, string target, Action<string> log)
    {
        try
        {
            lock (Sync)
            {
                var config = Plugin.Instance.Configuration;
                var raw = config.RawSeriesTranslationTable ?? string.Empty;

                // 能走到这里说明查表时还没有这一行；现在有了就是另一个并发线程先写完，采用先到的译文。
                // 同一次全库刷新里，一个系列的十几部影片并发走到这条链路是常态而不是边角。
                // 表值为空或 null 同样不覆盖：那是用户「这个系列保留原文」的显式意图。
                if (Lookup(raw, source, out var existing))
                {
                    log?.Invoke($"Series translation row appeared meanwhile, reuse it: {source} => {existing}");
                    return existing;
                }

                var next = Append(raw, source, target);
                if (next != raw)
                {
                    config.RawSeriesTranslationTable = next;
                    Plugin.Instance.UpdateConfiguration(config);
                    log?.Invoke($"Persist series translation: {source} => {target}");
                }

                return target;
            }
        }
        catch (Exception e)
        {
            // 持久化失败只影响下次要不要再调一次引擎，不改变本次刮削结果。
            log?.Invoke($"Persist series translation failed: {e.Message}");
            return target;
        }
    }
#endif
}
