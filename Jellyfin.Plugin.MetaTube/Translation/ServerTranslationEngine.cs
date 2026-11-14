using System.Collections.Specialized;
using System.Text;
using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>
/// 真正的引擎调用：限速、重试、谷歌免费分块与超时都在这里；失败抛异常，
/// 由 TranslationEngineChain 决定回退。主引擎固定 OpenAI 兼容接口。
/// </summary>
internal sealed class ServerTranslationEngine : ITranslationEngine
{
    private const string AutoLanguageCode = "auto";

    /// <summary>
    /// Maximum characters sent to the free Google endpoint in a single request.
    /// Japanese text is URL-encoded to roughly nine times its length, so keeping
    /// this small avoids Google rejecting the request for being too long.
    /// </summary>
    private const int FreeChunkLength = 600;

    /// <summary>Hard timeout for a single free translation request (includes retries).</summary>
    private static readonly TimeSpan FreeTimeout = TimeSpan.FromSeconds(20);

    private static readonly SemaphoreSlim Semaphore = new(1);

    private static PluginConfiguration Configuration => Plugin.Instance.Configuration;

    private enum Engine
    {
        OpenAi,
        Baidu,
        GoogleFree
    }

    public Task<string> TranslateWithOpenAiAsync(string text, CancellationToken cancellationToken)
    {
        return TranslateTextAsync(text, Engine.OpenAi, cancellationToken);
    }

    public Task<string> TranslateWithSecondaryAsync(string text, SecondaryTranslationEngine engine,
        CancellationToken cancellationToken)
    {
        return engine switch
        {
            SecondaryTranslationEngine.Baidu => TranslateTextAsync(text, Engine.Baidu, cancellationToken),
            SecondaryTranslationEngine.GoogleFree => TranslateWithFreeAsync(text, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(engine), engine, null)
        };
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
            return await TranslateTextAsync(text, Engine.GoogleFree, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Free translate timeout after {FreeTimeout.TotalSeconds:0}s");
        }
    }

    private static async Task<string> TranslateTextAsync(string q, Engine engine,
        CancellationToken cancellationToken)
    {
        int millisecondsDelay;
        var nv = new NameValueCollection();
        switch (engine)
        {
            case Engine.Baidu:
                millisecondsDelay = 1000; // Limit Baidu API request rate to 1 rps.
                nv.Add(new NameValueCollection
                {
                    { "baidu-app-id", Configuration.BaiduAppId },
                    { "baidu-app-key", Configuration.BaiduAppKey }
                });
                break;
            case Engine.GoogleFree:
                millisecondsDelay = 100;
                nv.Add(new NameValueCollection());
                break;
            case Engine.OpenAi:
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
                    .TranslateAsync(q, AutoLanguageCode, TranslationHelper.TargetLanguage, engine.ToString(), nv,
                        cancellationToken)
                    .ConfigureAwait(false)).TranslatedText;
            }

            return await RetryAsync(TranslateWithDelay, 5).ConfigureAwait(false);
        }
        finally
        {
            Semaphore.Release();
        }
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
