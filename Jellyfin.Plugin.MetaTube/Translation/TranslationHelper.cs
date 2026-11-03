using System.Collections.Specialized;
using System.Text;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

public static class TranslationHelper
{
    private const string AutoLanguageCode = "auto";
    private const string JapaneseLanguageCode = "ja";

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

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    public static async Task TranslateAsync(MovieInfo m, string to, CancellationToken cancellationToken,
        Action<string> log = null)
    {
        if (string.Equals(to, JapaneseLanguageCode, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"language not allowed: {to}");

        log ??= _ => { };

        await LogResolvedModelAsync(cancellationToken, log).ConfigureAwait(false);

        if (Configuration.TranslationMode.HasFlag(TranslationMode.Title) &&
            !string.IsNullOrWhiteSpace(m.Title) && !ShouldSkipTranslation(m.Title, to))
            m.Title = await TranslateFieldSafeAsync(m.Title, to, cancellationToken, log).ConfigureAwait(false);

        if (Configuration.TranslationMode.HasFlag(TranslationMode.Summary) &&
            !string.IsNullOrWhiteSpace(m.Summary) && !ShouldSkipTranslation(m.Summary, to))
            m.Summary = await TranslateFieldSafeAsync(m.Summary, to, cancellationToken, log).ConfigureAwait(false);

        if (Configuration.EnableSeriesTranslation &&
            !string.IsNullOrWhiteSpace(m.Series))
            m.Series = await TranslateSeriesAsync(m.Series, to, cancellationToken, log).ConfigureAwait(false);
    }

    /// <summary>
    /// 系列名单独一条链路：先查持久化对照表，命中就不再调引擎（含「表值为空 = 保留原文」）。
    /// 合集名、系列标签与 {series} 模板变量都取这一个值，所以译法必须每次都一样。
    /// </summary>
    private static async Task<string> TranslateSeriesAsync(string series, string to,
        CancellationToken cancellationToken, Action<string> log)
    {
        if (SeriesTranslationStore.TryGetValue(series, out var cached))
            return string.IsNullOrWhiteSpace(cached) ? series : cached;

        // 目标语言是中文且系列名已含汉字（iqqtv/airav 直接给中文系列名）时省一次调用。
        if (ShouldSkipTranslation(series, to))
            return series;

        var translated = await TranslateFieldSafeAsync(series, to, cancellationToken, log).ConfigureAwait(false);
        // 先裁再比对：不裁的话 " 甲 " != "甲" 会被当成一次成功翻译，把带空白的串当译文入库。
        translated = translated?.Trim();
        // 引擎失败时 TranslateFieldSafeAsync 原样返回，此时不写表，免得把原文当译文固化下来。
        if (string.IsNullOrWhiteSpace(translated) || translated == series)
            return series;

        // 译文要直接当合集名用，带换行就存不进按行存的译文表、也建不出正常合集；宁可保留原文。
        if (translated.Contains('\n') || translated.Contains('\r'))
        {
            log($"Translated series contains line breaks, keep original: {series}");
            return series;
        }

#if !__EMBY__
        // 生效译文可能不是引擎这次给的：同一系列的另一部影片可能已经先写了一行（Persist 先到先得）。
        var effective = SeriesTranslationStore.Persist(series, translated, log);
        return string.IsNullOrWhiteSpace(effective) ? series : effective;
#else
        return translated;
#endif
    }

    /// <summary>
    /// 目标语言为中文且文本已含中文字符时跳过翻译：内容已是中文
    /// （如 iqqtv / airav 直接提供的中文标题与剧情），省一次机器翻译调用。
    /// </summary>
    private static bool ShouldSkipTranslation(string text, string to)
    {
        return IsChineseTarget(to) && ContainsChinese(text);
    }

    private static bool IsChineseTarget(string language)
    {
        if (string.IsNullOrWhiteSpace(language))
            return false;
        return language.Equals("zh", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("zh-Hant", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("chs", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("cht", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("chi", StringComparison.OrdinalIgnoreCase) ||
               language.Equals("zho", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsChinese(string text)
    {
        foreach (var c in text)
        {
            if (c >= '\u4e00' && c <= '\u9fff')
                return true;
        }
        return false;
    }

    /// <summary>
    /// Translates a single field and keeps the original text on failure, so that a
    /// failing title does not prevent the summary from being translated.
    /// </summary>
    private static async Task<string> TranslateFieldSafeAsync(string text, string to,
        CancellationToken cancellationToken, Action<string> log)
    {
        try
        {
            return await TranslateFieldAsync(text, to, cancellationToken, log).ConfigureAwait(false);
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

    private static async Task<string> TranslateFieldAsync(string text, string to,
        CancellationToken cancellationToken, Action<string> log)
    {
        var engine = Configuration.TranslationEngine;

        if (engine != TranslationEngine.GoogleFree)
            return await TranslateTextAsync(text, to, engine, cancellationToken).ConfigureAwait(false);

        try
        {
            return await TranslateWithFreeAsync(text, to, cancellationToken).ConfigureAwait(false);
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

            return await TranslateTextAsync(text, to, TranslationEngine.OpenAi, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Splits long text into chunks and translates them one by one, then merges the result.
    /// </summary>
    private static async Task<string> TranslateWithFreeAsync(string text, string to,
        CancellationToken cancellationToken)
    {
        var chunks = SplitText(text, FreeChunkLength);
        if (chunks.Count == 1)
            return await TranslateChunkAsync(chunks[0], to, cancellationToken).ConfigureAwait(false);

        var builder = new StringBuilder(text.Length + 16);
        foreach (var chunk in chunks)
            builder.Append(await TranslateChunkAsync(chunk, to, cancellationToken).ConfigureAwait(false));

        return builder.ToString();
    }

    private static async Task<string> TranslateChunkAsync(string text, string to,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(FreeTimeout);

        try
        {
            return await TranslateTextAsync(text, to, TranslationEngine.GoogleFree, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Free translate timeout after {FreeTimeout.TotalSeconds:0}s");
        }
    }

    private static async Task<string> TranslateTextAsync(string q, string to, TranslationEngine engine,
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
                    .TranslateAsync(q, AutoLanguageCode, to, engine.ToString(), nv, cancellationToken)
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
