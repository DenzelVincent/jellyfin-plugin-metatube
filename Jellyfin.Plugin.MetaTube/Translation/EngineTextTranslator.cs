namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>术语用的引擎入口：与标题/简介共用限速、重试与免费端点降级，失败时抛异常。</summary>
internal sealed class EngineTextTranslator : ITextTranslator
{
    public Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
    {
        return TranslationHelper.TranslateTermAsync(text, cancellationToken);
    }
}
