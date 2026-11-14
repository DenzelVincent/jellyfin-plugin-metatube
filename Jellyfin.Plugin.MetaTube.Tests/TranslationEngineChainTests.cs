using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Translation;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 主辅引擎编排：主引擎（OpenAI）优先，失败或未配置回退辅引擎（百度/谷歌免费），
/// 辅引擎可选「无」；都不可用/都失败抛异常；调用方取消直接上抛、不触发回退。
/// </summary>
public class TranslationEngineChainTests
{
    [Fact]
    public async Task PrimarySucceeds_ReturnsPrimaryTextAndEngine()
    {
        var engine = new FakeEngine { OpenAiResult = "主引擎译文" };
        var chain = new TranslationEngineChain(engine, () => Config(key: "k"));

        var result = await chain.TranslateAsync("原文", CancellationToken.None);

        Assert.Equal("主引擎译文", result.Text);
        Assert.Equal("OpenAi", result.Engine);
        Assert.Equal(1, engine.OpenAiCalls);
        Assert.Equal(0, engine.SecondaryCalls);
    }

    [Fact]
    public async Task PrimaryFails_FallsBackToBaidu()
    {
        var engine = new FakeEngine
        {
            OpenAiError = new Exception("openai down"),
            SecondaryResult = "百度译文"
        };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.Baidu, baiduId: "id", baiduKey: "key"));

        var result = await chain.TranslateAsync("原文", CancellationToken.None);

        Assert.Equal("百度译文", result.Text);
        Assert.Equal("Baidu", result.Engine);
        Assert.Equal(SecondaryTranslationEngine.Baidu, engine.LastSecondaryEngine);
    }

    [Fact]
    public async Task PrimaryFails_FallsBackToGoogleFree()
    {
        var engine = new FakeEngine
        {
            OpenAiError = new Exception("openai down"),
            SecondaryResult = "谷歌译文"
        };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.GoogleFree));

        var result = await chain.TranslateAsync("原文", CancellationToken.None);

        Assert.Equal("谷歌译文", result.Text);
        Assert.Equal("GoogleFree", result.Engine);
        Assert.Equal(SecondaryTranslationEngine.GoogleFree, engine.LastSecondaryEngine);
    }

    [Fact]
    public async Task PrimaryNotConfigured_UsesSecondary()
    {
        var engine = new FakeEngine { SecondaryResult = "谷歌译文" };
        var chain = new TranslationEngineChain(engine, () => Config(
            secondary: SecondaryTranslationEngine.GoogleFree));

        var result = await chain.TranslateAsync("原文", CancellationToken.None);

        Assert.Equal("谷歌译文", result.Text);
        Assert.Equal(0, engine.OpenAiCalls);
        Assert.Equal(1, engine.SecondaryCalls);
    }

    [Fact]
    public async Task PrimaryFails_SecondaryNone_Throws()
    {
        var engine = new FakeEngine { OpenAiError = new Exception("openai down") };
        var chain = new TranslationEngineChain(engine, () => Config(key: "k"));

        await Assert.ThrowsAsync<Exception>(() => chain.TranslateAsync("原文", CancellationToken.None));
        Assert.Equal(0, engine.SecondaryCalls);
    }

    [Fact]
    public async Task NoEngineConfigured_Throws()
    {
        var engine = new FakeEngine();
        var chain = new TranslationEngineChain(engine, () => Config());

        await Assert.ThrowsAsync<Exception>(() => chain.TranslateAsync("原文", CancellationToken.None));
        Assert.Equal(0, engine.OpenAiCalls);
        Assert.Equal(0, engine.SecondaryCalls);
    }

    [Fact]
    public async Task BothEnginesFail_Throws()
    {
        var engine = new FakeEngine
        {
            OpenAiError = new Exception("openai down"),
            SecondaryError = new Exception("baidu down")
        };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.Baidu, baiduId: "id", baiduKey: "key"));

        var error = await Assert.ThrowsAsync<Exception>(() => chain.TranslateAsync("原文", CancellationToken.None));
        Assert.Contains("openai down", error.Message);
        Assert.Contains("baidu down", error.Message);
    }

    [Fact]
    public async Task PrimaryCancelled_PropagatesWithoutFallback()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var engine = new FakeEngine { OpenAiError = new OperationCanceledException(cts.Token) };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.GoogleFree));

        await Assert.ThrowsAsync<OperationCanceledException>(() => chain.TranslateAsync("原文", cts.Token));
        Assert.Equal(0, engine.SecondaryCalls);
    }

    [Fact]
    public async Task SecondaryCancelled_Propagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var engine = new FakeEngine
        {
            OpenAiError = new Exception("openai down"),
            SecondaryError = new OperationCanceledException(cts.Token)
        };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.GoogleFree));

        await Assert.ThrowsAsync<OperationCanceledException>(() => chain.TranslateAsync("原文", cts.Token));
    }

    [Fact]
    public async Task PrimaryTimeoutWithoutCallerCancellation_FallsBack()
    {
        // 引擎内部超时（调用方没取消）按失败处理，仍应回退辅引擎。
        var engine = new FakeEngine
        {
            OpenAiError = new OperationCanceledException(),
            SecondaryResult = "谷歌译文"
        };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.GoogleFree));

        var result = await chain.TranslateAsync("原文", CancellationToken.None);

        Assert.Equal("谷歌译文", result.Text);
    }

    [Fact]
    public async Task BaiduWithoutCredentials_IsUnavailable()
    {
        var engine = new FakeEngine { OpenAiError = new Exception("openai down") };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.Baidu));

        await Assert.ThrowsAsync<Exception>(() => chain.TranslateAsync("原文", CancellationToken.None));
        Assert.Equal(0, engine.SecondaryCalls);
    }

    [Fact]
    public async Task BaiduWithOnlyAppId_IsUnavailable()
    {
        var engine = new FakeEngine { OpenAiError = new Exception("openai down") };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.Baidu, baiduId: "id"));

        await Assert.ThrowsAsync<Exception>(() => chain.TranslateAsync("原文", CancellationToken.None));
        Assert.Equal(0, engine.SecondaryCalls);
    }

    [Fact]
    public async Task FallbackIsLogged()
    {
        var engine = new FakeEngine { OpenAiError = new Exception("openai down") };
        var chain = new TranslationEngineChain(engine, () => Config(
            key: "k", secondary: SecondaryTranslationEngine.GoogleFree));
        var logs = new List<string>();

        await chain.TranslateAsync("原文", CancellationToken.None, logs.Add);

        Assert.Contains(logs, message => message.Contains("openai down"));
    }

    [Fact]
    public void DefaultConfig_SecondaryIsNone()
    {
        Assert.Equal(SecondaryTranslationEngine.None, new PluginConfiguration().SecondaryTranslationEngine);
    }

    private static PluginConfiguration Config(string key = "",
        SecondaryTranslationEngine secondary = SecondaryTranslationEngine.None,
        string baiduId = "", string baiduKey = "")
    {
        return new PluginConfiguration
        {
            OpenAiApiKey = key,
            SecondaryTranslationEngine = secondary,
            BaiduAppId = baiduId,
            BaiduAppKey = baiduKey
        };
    }

    private sealed class FakeEngine : ITranslationEngine
    {
        public string OpenAiResult { get; set; } = "主引擎译文";
        public Exception OpenAiError { get; set; }
        public string SecondaryResult { get; set; } = "辅引擎译文";
        public Exception SecondaryError { get; set; }
        public int OpenAiCalls { get; private set; }
        public int SecondaryCalls { get; private set; }
        public SecondaryTranslationEngine LastSecondaryEngine { get; private set; }

        public Task<string> TranslateWithOpenAiAsync(string text, CancellationToken cancellationToken)
        {
            OpenAiCalls++;
            return OpenAiError != null
                ? Task.FromException<string>(OpenAiError)
                : Task.FromResult(OpenAiResult);
        }

        public Task<string> TranslateWithSecondaryAsync(string text, SecondaryTranslationEngine engine,
            CancellationToken cancellationToken)
        {
            SecondaryCalls++;
            LastSecondaryEngine = engine;
            return SecondaryError != null
                ? Task.FromException<string>(SecondaryError)
                : Task.FromResult(SecondaryResult);
        }
    }
}
