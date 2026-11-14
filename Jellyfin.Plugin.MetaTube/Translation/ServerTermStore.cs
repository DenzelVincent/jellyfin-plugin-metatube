namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>把服务端术语字典接到 ApiClient；失败原样抛出，由 TermTranslationService 降级。</summary>
internal sealed class ServerTermStore : ITermStore
{
    public async Task<string> LookupAsync(TermKind kind, string source, CancellationToken cancellationToken)
    {
        var info = await ApiClient
            .LookupTranslationTermAsync(KindName(kind), source, TranslationHelper.TargetLanguage, cancellationToken)
            .ConfigureAwait(false);
        return info?.Found == true ? info.TranslatedText : null;
    }

    public async Task<string> SaveAsync(TermKind kind, string source, string translatedText, string engine,
        CancellationToken cancellationToken)
    {
        // engine 记实际产出译文的引擎（主/辅回退后可能不同），不是配置里选的那个。
        var info = await ApiClient
            .SaveTranslationTermAsync(KindName(kind), source, TranslationHelper.TargetLanguage, translatedText,
                engine, cancellationToken)
            .ConfigureAwait(false);
        return info?.TranslatedText;
    }

    internal static string KindName(TermKind kind)
    {
        return kind switch
        {
            TermKind.Series => "series",
            TermKind.Genre => "genre",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
