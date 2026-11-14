namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员名拆分。
///
/// 影片来源常见写法是「A（B）」：B 是搜索更准确的艺名（含假名写法），A 是数据源
/// 可能已转换好的汉字名（进候选池）。没有括号时整串就是 B。原始字符串不删改，
/// 拆分结果只用于搜索与候选池；显示名由服务端艺名优选决定。
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
    /// 演员条目刷新元数据时的名字选择：已有名字（手动修改或刮削写入）优先保留，
    /// 名字为空时用字典里的艺名，最后退回演员接口名。
    /// </summary>
    public static string ResolvePersonDisplay(string existingName, string fallbackName, string stageName)
    {
        var existing = existingName?.Trim();
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var stage = stageName?.Trim();
        return string.IsNullOrWhiteSpace(stage) ? fallbackName : stage;
    }
}
