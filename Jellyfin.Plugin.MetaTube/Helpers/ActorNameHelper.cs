namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员名拆分。
///
/// 影片来源常见写法是「A（B）」：B 是搜索更准确的艺名（含假名写法），A 是数据源
/// 可能已转换好的汉字名（直接当译名）。没有括号时整串就是 B。原始字符串不删改，
/// 拆分结果只用于搜索、显示与入库。
/// </summary>
public static class ActorNameHelper
{
    public sealed class Parts
    {
        public string DisplayName { get; set; }
        public string SearchName { get; set; }
    }

    /// <summary>
    /// 拆分「A（B）」：DisplayName = A，SearchName = B；没有括号或括号内容为空时两者相同。
    /// </summary>
    public static Parts Parse(string raw)
    {
        var value = raw?.Trim();
        if (string.IsNullOrEmpty(value))
            return new Parts { DisplayName = raw, SearchName = raw };

        var last = value[value.Length - 1];
        if (last == '）' || last == ')')
        {
            var open = value.LastIndexOfAny(new[] { '（', '(' }, value.Length - 2);
            if (open > 0)
            {
                var inside = value.Substring(open + 1, value.Length - open - 2).Trim();
                var outside = value.Substring(0, open).Trim();
                if (inside.Length > 0 && outside.Length > 0 &&
                    outside.IndexOf('（') < 0 && outside.IndexOf('(') < 0)
                {
                    return new Parts { DisplayName = outside, SearchName = inside };
                }
            }
        }

        return new Parts { DisplayName = value, SearchName = value };
    }

    /// <summary>
    /// 页面/合集显示名：有译名时显示「译名（艺名）」，便于发现误译；没有译名或两者相同
    /// 时只显示艺名。艺名已包含在译名里（如 A（B）原样译回）时不再重复拼接。
    /// </summary>
    public static string FormatDisplay(string stageName, string translatedName)
    {
        var stage = (stageName ?? string.Empty).Trim();
        var translated = (translatedName ?? string.Empty).Trim();

        if (translated.Length == 0)
            return stage;
        if (stage.Length == 0)
            return translated;
        if (string.Equals(translated, stage, StringComparison.Ordinal) ||
            translated.IndexOf(stage, StringComparison.Ordinal) >= 0)
            return translated;

        return $"{translated}（{stage}）";
    }

    /// <summary>
    /// 不同演员译成同一个名字时，后出现的那位回退为原名，避免 Jellyfin 按名字合并。
    /// 原名也已被占用时只能保留译文（此时合并无法避免）。
    /// </summary>
    public static string[] ResolveTranslatedCollisions(string[] sources, string[] translated)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var result = new string[translated.Length];
        for (var i = 0; i < translated.Length; i++)
        {
            var candidate = translated[i];
            if (!string.IsNullOrWhiteSpace(candidate) && used.Add(candidate))
            {
                result[i] = candidate;
                continue;
            }

            var fallback = sources[i];
            if (!string.IsNullOrWhiteSpace(fallback) && used.Add(fallback))
            {
                result[i] = fallback;
                continue;
            }

            result[i] = candidate;
        }

        return result;
    }
}
