using System.ComponentModel;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>辅引擎：主引擎（OpenAI）失败或未配置时回退到这里；None 表示不回退。</summary>
public enum SecondaryTranslationEngine
{
    [Description("无")]
    None,

    [Description("百度")]
    Baidu,

    [Description("谷歌（免费）")]
    GoogleFree
}
