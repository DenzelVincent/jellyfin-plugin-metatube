namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员名拆分与角色分类。
///
/// 影片来源常见写法是「汉字名（括号内名字）」：括号内通常是搜索更准确的艺名/
/// 假名写法，页面显示用括号外的汉字名。原始字符串不删改，拆分结果只用于显示、
/// 搜索与入表；别名关系由服务端 actor_name 表维护。
/// </summary>
public static class ActorNameHelper
{
    public const string RoleStage = "stage";
    public const string RoleKana = "kana";
    public const string RoleRomaji = "romaji";
    public const string RoleAlias = "alias";

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
    /// 按字符组成分类：纯假名 → kana，纯拉丁字母/数字 → romaji，含汉字等其它字符 → stage，
    /// 混合或空值 → alias。
    /// </summary>
    public static string Classify(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return RoleAlias;

        var hasKana = false;
        var hasLatin = false;
        var hasOther = false;
        foreach (var c in name)
        {
            if (c == ' ' || c == '\u3000' || c == '・' || c == '.' || c == '-' || c == '\'')
                continue;

            // 平假名 / 片假名（含长音符 ー）。
            if (c >= '\u3040' && c <= '\u30ff')
            {
                hasKana = true;
                continue;
            }

            if ((c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z'))
            {
                hasLatin = true;
                continue;
            }

            hasOther = true;
        }

        if (hasOther)
            return RoleStage;
        if (hasKana && !hasLatin)
            return RoleKana;
        if (hasLatin && !hasKana)
            return RoleRomaji;
        return RoleAlias;
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
