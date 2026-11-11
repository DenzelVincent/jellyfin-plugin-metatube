using Jellyfin.Plugin.MetaTube.Translation;
#if __EMBY__
using System.ComponentModel;
using Emby.Web.GenericEdit;
using MediaBrowser.Model.Attributes;

#else
using MediaBrowser.Model.Plugins;
#endif

namespace Jellyfin.Plugin.MetaTube.Configuration;

#if __EMBY__
public class PluginConfiguration : EditableOptionsBase
{
    public override string EditorTitle => Plugin.ProviderName;
#else
public class PluginConfiguration : BasePluginConfiguration
{
#endif

#if __EMBY__
    [DisplayName("服务器地址")]
    [Description("MetaTube 服务端的完整地址，建议使用 HTTPS 协议。")]
    [Required]
#endif
    public string Server { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("访问令牌")]
    [Description("MetaTube 服务端的访问令牌；若后端未设置令牌则留空。")]
#endif
    public string Token { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("启用自动更新")]
    [Description("通过计划任务自动更新插件。")]
    public bool EnableAutoUpdate { get; set; } = true;
#endif

#if __EMBY__
    [DisplayName("启用合集")]
    [Description("按系列自动创建合集。")]
#endif
    public bool EnableCollections { get; set; } = false;

#if !__EMBY__
    /// <summary>
    /// 演员合集开关。合集由「同步演员合集」计划任务维护，不在刮削链路里写，
    /// 关掉开关只停任务，已生成的合集不会被删除（设计 4.3）。
    /// </summary>
    public bool EnableActorCollections { get; set; } = false;

    /// <summary>
    /// 演员合集的媒体库白名单（库 Guid 字符串）。空表示全部媒体库，保持旧行为。
    /// 只影响演员合集与「无演员影片」合集的成员范围：范围外的 MetaTube 影片会从已有合集移除，
    /// 范围外的新影片不再被收进来；刮削本身不受影响。
    /// </summary>
    public List<string> ActorCollectionLibraryIds { get; set; } = new();

    /// <summary>
    /// 最小片数阈值，默认 3，设为 1 即每位演员都建。
    /// 只门控「新建」：跌破阈值不回收已有合集，已存在的合集继续对齐成员（设计 D12）。
    /// </summary>
    public int ActorCollectionMinMovieCount { get; set; } = 3;

    /// <summary>
    /// 把合集内存活的影片数写进合集自己的 CommunityRating。
    /// 12.x 没有「子项数量」这个排序字段（ItemSortBy 里没有，未知值会静默退回 SortName），
    /// 而 CommunityRating 是条目级单列、BoxSet 本来用不到、且前端不做 0~10 钳制，
    /// 所以借它承载片数：库页排序选「公众评分」即按片数排，星星位置显示的数字就是片数。
    /// 关掉时把写过的值清回 null，不留假评分。
    /// </summary>
    public bool EnableCollectionCountRating { get; set; } = false;
#endif

#if __EMBY__
    [DisplayName("启用导演信息")]
    [Description("为对应影片的元数据添加导演信息。")]
#endif
    public bool EnableDirectors { get; set; } = true;

#if __EMBY__
    [DisplayName("启用评分")]
    [Description("显示来源网站的社区评分。")]
#endif
    public bool EnableRatings { get; set; } = true;

#if __EMBY__
    [DisplayName("启用预告片")]
    [Description("生成 strm 格式的在线预告片。")]
#endif
    public bool EnableTrailers { get; set; } = false;

#if __EMBY__
    [DisplayName("启用真实演员名")]
    [Description("使用 AVBASE 中的真实演员名进行搜索与替换。")]
#endif
    public bool EnableRealActorNames { get; set; } = false;

#if __EMBY__
    [DisplayName("启用角标")]
    [Description("在主图上添加中文字幕角标。")]
#endif
    public bool EnableBadges { get; set; } = false;

#if __EMBY__
    [DisplayName("角标图片地址")]
    [Description("自定义角标图片地址，建议使用 PNG 格式。（默认：zimu.png）")]
#endif
    public string BadgeUrl { get; set; } = "zimu.png";

#if __EMBY__
    [DisplayName("中文字幕关键字")]
    [Description("文件名末尾出现这些关键字时视为已内嵌中文字幕并添加角标，英文逗号分隔。默认：C,UC,ch,中文字幕")]
#endif
    public string ChineseSubtitleKeywords { get; set; } = "C,UC,ch,中文字幕";

#if __EMBY__
    [DisplayName("外挂中文字幕语言码")]
    [Description("同目录外挂字幕文件的语言码，英文逗号分隔。默认：chs,cht,chi,zh,zh-cn,zh-tw,zho")]
#endif
    public string ExternalChineseSubtitleLanguages { get; set; } = "chs,cht,chi,zh,zh-cn,zh-tw,zho";

#if __EMBY__
    [DisplayName("启用无码破解角标")]
    [Description("在主图右上角添加“无码破解”角标（需要服务端支持 uncensored.png 角标）。")]
#endif
    public bool EnableUncensoredBadges { get; set; } = false;

#if __EMBY__
    [DisplayName("无码破解关键字")]
    [Description("文件名带这些关键字时视为无码/破解并添加角标，英文逗号分隔。默认：U,UC,AI,uncensored,无码,破解,LTX,wm,restored,jasna")]
#endif
    public string UncensoredKeywords { get; set; } =
        "U,UC,AI,uncensored,无码,破解,LTX,wm,restored,jasna";

#if __EMBY__
    [DisplayName("无码破解角标图片")]
    [Description("无码破解角标图片名称，需服务端内置。（默认：uncensored.png）")]
#endif
    public string UncensoredBadgeUrl { get; set; } = "uncensored.png";

#if __EMBY__
    [DisplayName("主图比例")]
    [Description("主图的宽高比，设为负值则使用默认值。")]
#endif
    public double PrimaryImageRatio { get; set; } = -1;

#if __EMBY__
    [DisplayName("DMM 高清封面")]
    [Description("把 DMM 老 CDN 的封面地址改写为新 CDN 的高清版本（2184×1468）；取不到 cid 时保持原图。默认关闭。")]
#endif
    /// <summary>
    /// 上游 issue 653：JavBus/JAV321 的 cover_url 是 pics.dmm.co.jp 的 800×538 转存图，
    /// 同一作品在 awsimgsrc.dmm.co.jp 上有 2184×1468 版本。打开后只改写地址，由服务端照常取图。
    /// </summary>
    public bool EnableDmmHdCover { get; set; } = false;

#if __EMBY__
    [DisplayName("默认图片质量")]
    [Description("JPEG 图片的默认压缩质量，取值范围 0~100。（默认：90）")]
    [MinValue(0)]
    [MaxValue(100)]
    [Required]
#endif
    public int DefaultImageQuality { get; set; } = 90;

#if __EMBY__
    [DisplayName("启用影片源过滤")]
    [Description("过滤并排序来自各影片源的搜索结果。")]
#endif
    public bool EnableMovieProviderFilter { get; set; } = false;

#if __EMBY__
    [DisplayName("影片源过滤")]
    [Description(
        "源名称不区分大小写，越靠左优先级越高，多个源用英文逗号分隔。\n" +
        "可用源：JavBus, FANZA, JAV321, AVE, MGS, AVBASE, GFriends, 10musume, 1Pondo, AiRav, " +
        "AV-LEAGUE, AVSOX, C0930, Caribbeancom, CaribbeancomPR, DAHLIA, DUGA, FALENO, FC2, fc2hub, " +
        "FC2PPVDB, FREEJAVBT, Gcolle, Getchu, H0930, H4610, HEYZO, HeyDouga, IQQTV, JAVFREE, " +
        "KIN8, MadouQu, MINNANO, MURAMURA, MYWIFE, OFFICIAL, PACOPACOMAMA, Pcolle, SOD, ThePornDBMovie, " +
        "ThePornDBScene, TOKYO-HOT, Wikipedia。\n默认：JavBus,FANZA,JAV321,AVE,MGS,AVBASE,GFriends")]
#endif
    public string RawMovieProviderFilter
    {
        get => _movieProviderFilter?.Any() == true ? string.Join(',', _movieProviderFilter) : string.Empty;
        set => _movieProviderFilter = value?.Split(',').Select(s => s.Trim()).Where(s => s.Any())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public List<string> GetMovieProviderFilter()
    {
        return _movieProviderFilter;
    }

#if __EMBY__
    [DisplayName("启用字段级聚合")]
    [Description("跨源按字段取优：标题/简介/封面等可分别来自不同源。")]
#endif
    public bool EnableFieldAggregation { get; set; } = false;

#if __EMBY__
    [DisplayName("字段优先级")]
    [Description(
        "按字段指定取优源，格式：字段名:源1,源2;字段名:源1。字段名可用 Title、Summary、CoverURL、Director、Actors、Genres、Series、Maker、Label、Score、Runtime、ReleaseDate、PreviewImages 等。")]
#endif
    public string RawFieldPriority { get; set; } = string.Empty;

    /// <summary>
    /// 解析字段优先级配置，返回「字段名 → 按优先级排序的源列表」。
    /// </summary>
    public Dictionary<string, List<string>> GetFieldPriority()
    {
        return ParseFieldPriority(RawFieldPriority);
    }

    private static Dictionary<string, List<string>> ParseFieldPriority(string raw)
    {
        var result = new Dictionary<string, List<string>>();
        if (string.IsNullOrWhiteSpace(raw))
            return result;

        foreach (var entry in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var idx = entry.IndexOf(':');
            if (idx <= 0)
                continue;

            var field = entry[..idx].Trim();
            var providers = entry[(idx + 1)..]
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (field.Length == 0 || providers.Count == 0)
                continue;

            result[field] = providers;
        }

        return result;
    }

    // 越靠左优先级越高。FANZA 对新番收录最全，不能漏掉。
    private List<string> _movieProviderFilter = new()
    {
        "JavBus", "FANZA", "JAV321", "AVE", "MGS", "AVBASE", "GFriends"
    };

#if __EMBY__
    [DisplayName("启用模板")]
#endif
    public bool EnableTemplate { get; set; } = false;

#if __EMBY__
    [DisplayName("名称模板")]
#endif
    public string NameTemplate { get; set; } = DefaultNameTemplate;

#if __EMBY__
    [DisplayName("标语模板")]
#endif
    public string TaglineTemplate { get; set; } = DefaultTaglineTemplate;

    public static string DefaultNameTemplate => "{number} {title}";

    public static string DefaultTaglineTemplate => "配信開始日 {date}";

    // 翻译多选：勾选哪一项，页面与派生数据（合集/标签/Studio/模板）就用哪一项的译文。
    // 目标语言固定 zh-CN；全不勾 = 不翻译。
#if __EMBY__
    [DisplayName("翻译标题")]
#endif
    public bool TranslateTitle { get; set; } = false;

#if __EMBY__
    [DisplayName("翻译简介")]
#endif
    public bool TranslateSummary { get; set; } = false;

#if __EMBY__
    [DisplayName("翻译系列名")]
#endif
    public bool TranslateSeries { get; set; } = false;

#if __EMBY__
    [DisplayName("翻译类别")]
#endif
    public bool TranslateGenre { get; set; } = false;

#if __EMBY__
    [DisplayName("翻译演员名")]
#endif
    public bool TranslateActor { get; set; } = false;

#if __EMBY__
    [DisplayName("翻译引擎")]
#endif
    public TranslationEngine TranslationEngine { get; set; } = TranslationEngine.Baidu;

#if __EMBY__
    [DisplayName("百度 App ID")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppId { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("百度 App Key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Baidu)]
#endif
    public string BaiduAppKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("谷歌 API Key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("谷歌 API 地址")]
    [Description("自定义谷歌翻译 API 地址。（可选）")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.Google)]
#endif
    public string GoogleApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL API Key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("DeepL API 地址")]
    [Description("兼容 DeepL 的自定义 API 地址。（可选）")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.DeepL)]
#endif
    public string DeepLApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI API Key")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiKey { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI API 地址")]
    [Description("兼容 OpenAI 的自定义 API 地址，需包含版本路径（例如 https://api.openai.com/v1）。（可选）")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiApiUrl { get; set; } = string.Empty;

#if __EMBY__
    [DisplayName("OpenAI 模型")]
    [Description("留空时自动从 API 地址的 /v1/models 中选取首个可用对话模型；填写后优先使用填写的模型。")]
    [VisibleCondition(nameof(TranslationEngine), ValueCondition.IsEqual, TranslationEngine.OpenAi)]
#endif
    public string OpenAiModel { get; set; } = string.Empty;

}