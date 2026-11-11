namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员名规范化与译名撞名兜底。
///
/// 部分来源返回「柚井初美（ゆい はつみ）」这种「名字（读音）」格式。整串直接入库会让
/// 同一位演员在不同来源下变成两个 Person（Jellyfin 按名字匹配），翻译引擎也会把读音
/// 一起翻掉；演员搜索按整串也匹配不到。这里只删除末尾「括号内全是假名/拉丁字母/数字/
/// 空格」的注解，括号里含汉字（别名、组合、角色名）时原样保留，避免误伤区分信息。
/// </summary>
public static class ActorNameHelper
{
    public static string Normalize(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return name;

        var value = name.Trim();
        while (TryStripTrailingReading(value, out var stripped))
            value = stripped;

        return value;
    }

    public static string[] NormalizeAll(string[] names)
    {
        if (names == null)
            return null;

        var result = new string[names.Length];
        for (var i = 0; i < names.Length; i++)
            result[i] = Normalize(names[i]);

        return result;
    }

    /// <summary>
    /// 同一部片里两个不同演员译成同一个名字时，后出现的那位保留原名，
    /// 避免后续 Distinct() 把不同演员合并掉。原名也已被占用时只能保留译文
    /// （此时合并无法避免）。
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

    private static bool TryStripTrailingReading(string value, out string stripped)
    {
        stripped = null;
        if (value.Length < 3)
            return false;

        var last = value[value.Length - 1];
        if (last != '）' && last != ')')
            return false;

        var openIndex = value.LastIndexOfAny(new[] { '（', '(' }, value.Length - 2);
        if (openIndex <= 0)
            return false;

        var annotation = value.Substring(openIndex + 1, value.Length - openIndex - 2);
        if (!IsReading(annotation))
            return false;

        var head = value.Substring(0, openIndex).TrimEnd();
        // 嵌套括号（如 A（ゆい（はつみ）））不处理，避免把名字截坏。
        if (head.Length == 0 || head.IndexOf('（') >= 0 || head.IndexOf('(') >= 0)
            return false;

        stripped = head;
        return true;
    }

    private static bool IsReading(string annotation)
    {
        if (string.IsNullOrWhiteSpace(annotation))
            return false;

        foreach (var c in annotation)
        {
            if (c == ' ' || c == '\u3000' || c == '・' || c == '.' || c == '-' || c == '\'')
                continue;

            // 平假名 / 片假名（含长音符 ー）。
            if (c >= '\u3040' && c <= '\u30ff')
                continue;

            if ((c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'z') ||
                (c >= 'A' && c <= 'Z'))
                continue;

            return false;
        }

        return true;
    }
}
