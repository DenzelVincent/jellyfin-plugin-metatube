using System.Collections.Specialized;
using System.Text;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

public static class TranslationHelper
{
    /// <summary>目标语言固定为简体中文，配置页不再提供语言选择。</summary>
    public const string TargetLanguage = "zh-CN";

    private const string AutoLanguageCode = "auto";

    /// <summary>
    /// Maximum characters sent to the free Google endpoint in a single request.
    /// Japanese text is URL-encoded to roughly nine times its length, so keeping
    /// this small avoids Google rejecting the request for being too long.
    /// </summary>
    private const int FreeChunkLength = 600;

    /// <summary>
    /// Hard timeout for a single free translation request (includes retries).
    /// </summary>
    private static readonly TimeSpan FreeTimeout = TimeSpan.FromSeconds(20);

    private static readonly SemaphoreSlim Semaphore = new(1);

    // 进程级术语服务：同一演员/系列/类别在一次进程生命周期内只查一次库、只翻一次。
    private static readonly TermTranslationService Terms = new(new ServerTermStore(), new EngineTextTranslator());

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    /// <summary>7 个翻译项任一勾选即启用翻译；全不勾时完全不触碰元数据。</summary>
    public static bool IsEnabled
    {
        get
        {
            var config = Configuration;
            return config.TranslateTitle || config.TranslateSummary || config.TranslateSeries ||
                   config.TranslateGenre || config.TranslateActor || config.TranslateMaker ||
                   config.TranslateLabel;
        }
    }

    /// <summary>
    /// 按配置勾选项翻译影片元数据。标题/简介每次直译；系列、类别、片商、厂牌、演员走术语字典。
    /// 返回「译名 → 原名」的演员映射：PersonInfo 显示译名，但演员搜索（pid 与头像）
    /// 必须用服务端收录的原名。
    /// </summary>
    public static async Task<Dictionary<string, string>> TranslateMovieAsync(Metadata.MovieInfo m,
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

        if (config.TranslateMaker && !string.IsNullOrWhiteSpace(m.Maker))
            m.Maker = await Terms.ResolveAsync(TermKind.Maker, m.Maker, cancellationToken, log)
                .ConfigureAwait(false);

        if (config.TranslateLabel && !string.IsNullOrWhiteSpace(m.Label))
            m.Label = await Terms.ResolveAsync(TermKind.Label, m.Label, cancellationToken, log)
                .ConfigureAwait(false);

        if (config.TranslateGenre && m.Genres?.Any() == true)
        {
            var genres = new string[m.Genres.Length];
            for (var i = 0; i < m.Genres.Length; i++)
                genres[i] = await Terms.ResolveAsync(TermKind.Genre, m.Genres[i], cancellationToken, log)
                    .ConfigureAwait(false);
            m.Genres = genres;
        }

        var actorMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (config.TranslateActor && m.Actors?.Any() == true)
        {
            var actors = new string[m.Actors.Length];
            for (var i = 0; i < m.Actors.Length; i++)
            {
                var source = m.Actors[i];
                var translated = await Terms.ResolveAsync(TermKind.Actor, source, cancellationToken, log)
                    .ConfigureAwait(false);
                actors[i] = translated;
                // 两个原名译成同一个名字时保留第一个：PersonInfo 只能有一个 Name，
                // 去重后只剩一个演员条目，头像按第一条的原名取。
                if (!string.IsNullOrWhiteSpace(translated))
                    actorMap.TryAdd(translated, source);
            }
            m.Actors = actors;
        }

        return actorMap;
    }

    /// <summary>
    /// 术语用的直译入口：与标题/简介共用限速、重试与免费端点降级，
    /// 但失败必须抛出——TermTranslationService 靠异常区分「引擎失败」和「译名与原文相同」。
    /// </summary>
    internal static Task<string> TranslateTermAsync(string text, CancellationToken cancellationToken)
    {
        return TranslateFieldAsync(text, cancellationToken, _ => { });
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
            return await TranslateFieldAsync(text, cancellationToken, log).ConfigureAwait(false);
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
        // 只有真的会用到 OpenAI（选它，或免费端点失败时回落到它）才值得探测：
        // 探测是带 10 秒超时的真实请求，端点不可达时每张影片都可能白等一趟。
        if (config.TranslationEngine != TranslationEngine.OpenAi && !CanFallbackToAi())
            return;

        if (string.IsNullOrWhiteSpace(config.OpenAiApiUrl) || !string.IsNullOrWhiteSpace(config.OpenAiModel))
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

    private static async Task<string> TranslateFieldAsync(string text,
        CancellationToken cancellationToken, Action<string> log)
    {
        var engine = Configuration.TranslationEngine;

        if (engine != TranslationEngine.GoogleFree)
            return await TranslateTextAsync(text, engine, cancellationToken).ConfigureAwait(false);

        try
        {
            return await TranslateWithFreeAsync(text, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller cancelled (server shutting down / refresh aborted), do not fallback.
            throw;
        }
        catch (Exception e)
        {
            if (!CanFallbackToAi())
                throw;

            log($"Free translate failed: {e.Message}, fallback to OpenAI");

            return await TranslateTextAsync(text, TranslationEngine.OpenAi, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits long text into chunks and translates them one by one, then merges the result.
    /// </summary>
    private static async Task<string> TranslateWithFreeAsync(string text, CancellationToken cancellationToken)
    {
        var chunks = SplitText(text, FreeChunkLength);
        if (chunks.Count == 1)
            return await TranslateChunkAsync(chunks[0], cancellationToken).ConfigureAwait(false);

        var builder = new StringBuilder(text.Length + 16);
        foreach (var chunk in chunks)
            builder.Append(await TranslateChunkAsync(chunk, cancellationToken).ConfigureAwait(false));

        return builder.ToString();
    }

    private static async Task<string> TranslateChunkAsync(string text, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FreeTimeout);

        try
        {
            return await TranslateTextAsync(text, TranslationEngine.GoogleFree, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Free translate timeout after {FreeTimeout.TotalSeconds:0}s");
        }
    }

    private static async Task<string> TranslateTextAsync(string q, TranslationEngine engine,
        CancellationToken cancellationToken)
    {
        int millisecondsDelay;
        var nv = new NameValueCollection();
        switch (engine)
        {
            case TranslationEngine.Baidu:
                millisecondsDelay = 1000; // Limit Baidu API request rate to 1 rps.
                nv.Add(new NameValueCollection
                {
                    { "baidu-app-id", Configuration.BaiduAppId },
                    { "baidu-app-key", Configuration.BaiduAppKey }
                });
                break;
            case TranslationEngine.Google:
                millisecondsDelay = 100; // Limit Google API request rate to 10 rps.
                nv.Add(new NameValueCollection
                {
                    { "google-api-key", Configuration.GoogleApiKey },
                    { "google-api-url", Configuration.GoogleApiUrl }
                });
                break;
            case TranslationEngine.GoogleFree:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection());
                break;
            case TranslationEngine.DeepL:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection
                {
                    { "deepl-api-key", Configuration.DeepLApiKey },
                    { "deepl-api-url", Configuration.DeepLApiUrl }
                });
                break;
            case TranslationEngine.OpenAi:
                millisecondsDelay = 1000;
                var model = await OpenAiHelper.ResolveModelAsync(cancellationToken).ConfigureAwait(false);
                nv.Add(new NameValueCollection
                {
                    { "openai-api-key", Configuration.OpenAiApiKey },
                    { "openai-api-url", Configuration.OpenAiApiUrl },
                    { "openai-model", model }
                });
                break;
            default:
                throw new ArgumentException($"Invalid translation engine: {engine}");
        }

        await Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            async Task<string> TranslateWithDelay()
            {
                await Task.Delay(millisecondsDelay, cancellationToken).ConfigureAwait(false);
                return (await ApiClient
                    .TranslateAsync(q, AutoLanguageCode, TargetLanguage, engine.ToString(), nv, cancellationToken)
                    .ConfigureAwait(false)).TranslatedText;
            }

            return await RetryAsync(TranslateWithDelay, 5).ConfigureAwait(false);
        }
        finally
        {
            Semaphore.Release();
        }
    }

    private static bool CanFallbackToAi()
    {
        if (Configuration.TranslationEngine == TranslationEngine.OpenAi)
            return false;

        return !string.IsNullOrWhiteSpace(Configuration.OpenAiApiKey);
    }

    /// <summary>
    /// Splits text into chunks no longer than <paramref name="maxLength"/>,
    /// preferring natural boundaries so sentences are not cut in half.
    /// </summary>
    private static List<string> SplitText(string text, int maxLength)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            chunks.Add(text ?? string.Empty);
            return chunks;
        }

        var start = 0;
        while (start < text.Length)
        {
            if (text.Length - start <= maxLength)
            {
                chunks.Add(text.Substring(start));
                break;
            }

            var end = start + maxLength;
            var cut = FindCutPoint(text, start, end);
            chunks.Add(text.Substring(start, cut - start));
            start = cut;
        }

        return chunks;
    }

    private static int FindCutPoint(string text, int start, int end)
    {
        for (var i = end; i > start; i--)
        {
            switch (text[i - 1])
            {
                case '\n':
                case '>': // End of an inline tag such as <br>.
                case '。':
                case '！':
                case '？':
                case '.':
                case '!':
                case '?':
                    return i;
            }
        }

        return end;
    }

    private static async Task<T> RetryAsync<T>(Func<Task<T>> func, int retryCount)
    {
        while (true)
        {
            try
            {
                return await func().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Never swallow cancellation, otherwise timeouts and shutdown are ignored.
                throw;
            }
            catch when (--retryCount > 0)
            {
            }
        }
    }
}
