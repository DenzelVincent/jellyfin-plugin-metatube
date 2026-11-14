using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

public static class TranslationHelper
{
    /// <summary>目标语言固定为简体中文，配置页不再提供语言选择。</summary>
    public const string TargetLanguage = "zh-CN";

    // 主辅引擎编排：主引擎 OpenAI 优先，失败或未配置时回退辅引擎（百度/谷歌免费/无）。
    private static readonly TranslationEngineChain Chain =
        new(new ServerTranslationEngine(), () => Plugin.Instance.Configuration);

    // 进程级术语服务：同一系列/类别在一次进程生命周期内只查一次库、只翻一次。
    private static readonly TermTranslationService Terms = new(new ServerTermStore(), new EngineTextTranslator());

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    /// <summary>4 个翻译项任一勾选即启用翻译；全不勾时完全不触碰元数据。</summary>
    public static bool IsEnabled
    {
        get
        {
            var config = Configuration;
            return config.TranslateTitle || config.TranslateSummary || config.TranslateSeries ||
                   config.TranslateGenre;
        }
    }

    /// <summary>
    /// 按配置勾选项翻译影片元数据。标题/简介每次直译；系列、类别走术语字典。
    /// 演员名不翻译：由 <see cref="ActorIdentityResolver"/> 解析身份并取服务端优选艺名。
    /// </summary>
    public static async Task TranslateMovieAsync(Metadata.MovieInfo m,
        CancellationToken cancellationToken, Action<string> log = null)
    {
        log ??= _ => { };
        var config = Configuration;

        await LogResolvedModelAsync(cancellationToken, log).ConfigureAwait(false);

        if (config.TranslateTitle && !string.IsNullOrWhiteSpace(m.Title))
            m.Title = await TranslateFieldSafeAsync(m.Title, cancellationToken, log).ConfigureAwait(false);

        if (config.TranslateSummary && !string.IsNullOrWhiteSpace(m.Summary))
            m.Summary = await TranslateFieldSafeAsync(m.Summary, cancellationToken, log).ConfigureAwait(false);

        if (config.TranslateSeries && !string.IsNullOrWhiteSpace(m.Series))
            m.Series = await Terms.ResolveAsync(TermKind.Series, m.Series, cancellationToken, log)
                .ConfigureAwait(false);

        if (config.TranslateGenre && m.Genres?.Any() == true)
        {
            var genres = new string[m.Genres.Length];
            for (var i = 0; i < m.Genres.Length; i++)
                genres[i] = await Terms.ResolveAsync(TermKind.Genre, m.Genres[i], cancellationToken, log)
                    .ConfigureAwait(false);
            m.Genres = genres;
        }
    }

    /// <summary>
    /// 术语用的直译入口：与标题/简介共用主辅引擎链，
    /// 但失败必须抛出——TermTranslationService 靠异常区分「引擎失败」和「译名与原文相同」。
    /// </summary>
    internal static Task<EngineTranslation> TranslateTermAsync(string text, CancellationToken cancellationToken)
    {
        return Chain.TranslateAsync(text, cancellationToken);
    }

    /// <summary>
    /// Translates a single field and keeps the original text on failure, so that a
    /// failing title does not prevent the summary from being translated.
    /// </summary>
    private static async Task<string> TranslateFieldSafeAsync(string text,
        CancellationToken cancellationToken, Action<string> log)
    {
        try
        {
            return (await Chain.TranslateAsync(text, cancellationToken, log).ConfigureAwait(false)).Text;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log($"Translate field error: {e.Message}, keep original text");
            return text;
        }
    }

    /// <summary>
    /// Pre-resolves the OpenAI model when a compatible endpoint is configured but
    /// the model field is left empty, so that the choice is visible in the logs.
    /// </summary>
    private static async Task LogResolvedModelAsync(CancellationToken cancellationToken, Action<string> log)
    {
        var config = Configuration;
        // 主引擎没配密钥就不会调 OpenAI，不值得探测：
        // 探测是带 10 秒超时的真实请求，端点不可达时每张影片都可能白等一趟。
        if (string.IsNullOrWhiteSpace(config.OpenAiApiKey) || !string.IsNullOrWhiteSpace(config.OpenAiModel))
            return;

        try
        {
            var model = await OpenAiHelper.ResolveModelAsync(cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(model))
                log($"OpenAI model not configured, auto detected: {model}");
            else
                log("OpenAI model not configured and auto detection failed");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log($"OpenAI model auto detection error: {e.Message}");
        }
    }
}
