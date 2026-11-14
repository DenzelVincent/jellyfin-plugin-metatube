using Jellyfin.Plugin.MetaTube.Configuration;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>翻译结果：文本 + 实际产出它的引擎名（术语字典入库时记录）。</summary>
public sealed record EngineTranslation(string Text, string Engine);

/// <summary>单个引擎的调用入口：限速、重试、分块等由实现负责；失败必须抛异常。</summary>
public interface ITranslationEngine
{
    Task<string> TranslateWithOpenAiAsync(string text, CancellationToken cancellationToken);

    Task<string> TranslateWithSecondaryAsync(string text, SecondaryTranslationEngine engine,
        CancellationToken cancellationToken);
}

/// <summary>
/// 主辅引擎编排：主引擎（OpenAI，密钥非空才启用）优先，失败或跳过时回退辅引擎
/// （百度需凭据、谷歌免费）；都不可用/都失败抛异常。调用方取消直接上抛，不触发回退。
/// </summary>
public sealed class TranslationEngineChain
{
    private readonly ITranslationEngine _engine;
    private readonly Func<PluginConfiguration> _configuration;

    public TranslationEngineChain(ITranslationEngine engine, Func<PluginConfiguration> configuration)
    {
        _engine = engine;
        _configuration = configuration;
    }

    public async Task<EngineTranslation> TranslateAsync(string text, CancellationToken cancellationToken,
        Action<string> log = null)
    {
        log ??= _ => { };
        var config = _configuration();
        Exception primaryError = null;

        // 主引擎：密钥非空才启用；未配置视为跳过，直接看辅引擎。
        if (!string.IsNullOrWhiteSpace(config.OpenAiApiKey))
        {
            try
            {
                var translated = await _engine.TranslateWithOpenAiAsync(text, cancellationToken)
                    .ConfigureAwait(false);
                return new EngineTranslation(translated, "OpenAi");
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                primaryError = e;
                log($"Primary translate failed: {e.Message}, try secondary engine");
            }
        }

        var secondary = config.SecondaryTranslationEngine;
        // 选了百度但凭据没填全：视为不可用，不发起请求。
        if (secondary == SecondaryTranslationEngine.Baidu &&
            (string.IsNullOrWhiteSpace(config.BaiduAppId) || string.IsNullOrWhiteSpace(config.BaiduAppKey)))
        {
            log("Secondary engine Baidu has no credentials, unavailable");
            secondary = SecondaryTranslationEngine.None;
        }

        if (secondary != SecondaryTranslationEngine.None)
        {
            try
            {
                var translated = await _engine.TranslateWithSecondaryAsync(text, secondary, cancellationToken)
                    .ConfigureAwait(false);
                return new EngineTranslation(translated, secondary.ToString());
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                throw new Exception("All translation engines failed: " +
                                    $"primary={primaryError?.Message ?? "not configured"}, secondary={e.Message}");
            }
        }

        throw new Exception(primaryError != null
            ? $"Primary translate failed and no secondary engine available: {primaryError.Message}"
            : "No translation engine configured");
    }
}
