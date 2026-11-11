using System.Collections.Concurrent;
using System.Globalization;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>术语类别，与服务端 translation_metadata.kind 一一对应。</summary>
public enum TermKind
{
    Actor,
    Series,
    Genre,
    Maker,
    Label
}

/// <summary>服务端术语字典的读写入口。失败时抛异常，由调用方决定是否降级。</summary>
public interface ITermStore
{
    Task<string> LookupAsync(TermKind kind, string source, CancellationToken cancellationToken);

    Task<string> SaveAsync(TermKind kind, string source, string translatedText, CancellationToken cancellationToken);
}

/// <summary>
/// 真正调用翻译引擎的入口。失败时必须抛异常：调用方靠它区分
/// 「引擎失败」和「引擎成功但译名与原文相同」——后者应当入库缓存。
/// </summary>
public interface ITextTranslator
{
    Task<string> TranslateAsync(string text, CancellationToken cancellationToken);
}

/// <summary>
/// 术语翻译链路：进程缓存 → 服务端字典 → 引擎 → 回写字典。
/// 标题/简介不走这里，只有会当名字用的短文本才需要全库统一译法。
/// </summary>
public sealed class TermTranslationService
{
    private readonly ITermStore _store;
    private readonly ITextTranslator _translator;
    private readonly ConcurrentDictionary<string, string> _cache;
    private int _storeDisabled;

    public TermTranslationService(ITermStore store, ITextTranslator translator,
        ConcurrentDictionary<string, string> cache = null)
    {
        _store = store;
        _translator = translator;
        _cache = cache ?? new ConcurrentDictionary<string, string>(StringComparer.Ordinal);
    }

    public async Task<string> ResolveAsync(TermKind kind, string source, CancellationToken cancellationToken,
        Action<string> log = null)
    {
        log ??= _ => { };
        if (string.IsNullOrWhiteSpace(source))
            return source;

        var trimmed = source.Trim();
        var cacheKey = BuildCacheKey(kind, trimmed);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        string hit = null;
        if (Volatile.Read(ref _storeDisabled) == 0)
        {
            try
            {
                hit = await _store.LookupAsync(kind, trimmed, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                // 旧服务端 404、网络或数据库错误都走这里：本进程后续跳过字典，
                // 避免每个术语都白等一次超时；翻译功能本身不受影响。
                DisableStore(e, log);
            }
        }

        if (!string.IsNullOrWhiteSpace(hit))
            return Remember(cacheKey, hit.Trim());

        string translated;
        try
        {
            translated = (await _translator.TranslateAsync(trimmed, cancellationToken).ConfigureAwait(false))?.Trim();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            // 引擎失败不缓存，下一部影片还能重试。
            log($"Term translate error: {kind} {trimmed} ({e.Message})");
            return trimmed;
        }

        if (string.IsNullOrWhiteSpace(translated))
            return trimmed;

        // 术语会直接当合集名、标签、演员名，带换行会把库里的名字弄坏。
        if (translated.Contains('\n') || translated.Contains('\r'))
        {
            log($"Term translation contains line breaks, keep original: {kind} {trimmed}");
            return trimmed;
        }

        string effective = null;
        if (Volatile.Read(ref _storeDisabled) == 0)
        {
            try
            {
                effective = await _store.SaveAsync(kind, trimmed, translated, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                DisableStore(e, log);
            }
        }

        // 并发下另一部影片可能先写了同一个术语，以服务端返回的生效值为准。
        return Remember(cacheKey, string.IsNullOrWhiteSpace(effective) ? translated : effective.Trim());
    }

    private string Remember(string key, string value)
    {
        _cache[key] = value;
        return value;
    }

    private void DisableStore(Exception error, Action<string> log)
    {
        // Interlocked 保证多个并发术语只打一条降级日志。
        if (Interlocked.Exchange(ref _storeDisabled, 1) == 0)
            log($"Translation dictionary unavailable, fall back to direct translation: {error.Message}");
    }

    private static string BuildCacheKey(TermKind kind, string source)
    {
        return ((int)kind).ToString(CultureInfo.InvariantCulture) + ":" + source.Trim().ToLowerInvariant();
    }
}
