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

    public async Task<string> SaveAsync(TermKind kind, string source, string translatedText,
        CancellationToken cancellationToken)
    {
        var info = await ApiClient
            .SaveTranslationTermAsync(KindName(kind), source, TranslationHelper.TargetLanguage, translatedText,
                Plugin.Instance.Configuration.TranslationEngine.ToString(), cancellationToken)
            .ConfigureAwait(false);
        return info?.TranslatedText;
    }

    internal static string KindName(TermKind kind)
    {
        return kind switch
        {
            TermKind.Actor => "actor",
            TermKind.Series => "series",
            TermKind.Genre => "genre",
            TermKind.Maker => "maker",
            TermKind.Label => "label",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };
    }
}
