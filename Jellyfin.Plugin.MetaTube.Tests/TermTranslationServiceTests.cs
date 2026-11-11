using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 术语链路：进程缓存 → 服务端字典 → 引擎 → 回写。
/// 关键是区分「引擎失败」和「译名与原文相同」：前者不缓存，后者必须入库。
/// </summary>
public class TermTranslationServiceTests
{
    [Fact]
    public async Task ResolveAsync_UsesServerHitWithoutCallingEngine()
    {
        var store = new FakeStore { LookupResult = "柚井初美" };
        var translator = new FakeTranslator();
        var service = new TermTranslationService(store, translator);

        var result = await service.ResolveAsync(TermKind.Actor, "Yui Hatsumi", CancellationToken.None);

        Assert.Equal("柚井初美", result);
        Assert.Equal(0, translator.Calls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task ResolveAsync_OnMissTranslatesAndSaves()
    {
        var store = new FakeStore();
        var translator = new FakeTranslator { Result = "柚井初美" };
        var service = new TermTranslationService(store, translator);

        var result = await service.ResolveAsync(TermKind.Actor, "Yui Hatsumi", CancellationToken.None);

        Assert.Equal("柚井初美", result);
        Assert.Equal(1, translator.Calls);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(("Yui Hatsumi", "柚井初美"), store.LastSaved);
    }

    [Fact]
    public async Task ResolveAsync_PrefersEffectiveValueReturnedByServer()
    {
        // 并发下另一部影片先写了同一术语，服务端保留首写译文。
        var store = new FakeStore { SaveResult = "柚井初美" };
        var translator = new FakeTranslator { Result = "柚井初美（新）" };
        var service = new TermTranslationService(store, translator);

        var result = await service.ResolveAsync(TermKind.Actor, "Yui Hatsumi", CancellationToken.None);

        Assert.Equal("柚井初美", result);
    }

    [Fact]
    public async Task ResolveAsync_CachesNormalizedTerm()
    {
        var store = new FakeStore();
        var translator = new FakeTranslator { Result = "柚井初美" };
        var service = new TermTranslationService(store, translator);

        var first = await service.ResolveAsync(TermKind.Actor, "  Yui Hatsumi  ", CancellationToken.None);
        var second = await service.ResolveAsync(TermKind.Actor, "yui hatsumi", CancellationToken.None);

        Assert.Equal("柚井初美", first);
        Assert.Equal("柚井初美", second);
        Assert.Equal(1, store.LookupCalls);
        Assert.Equal(1, translator.Calls);
    }

    [Fact]
    public async Task ResolveAsync_IsolatesCacheByKind()
    {
        var store = new FakeStore();
        var translator = new FakeTranslator();
        var service = new TermTranslationService(store, translator);
        var calls = 0;
        translator.Translate = _ => (++calls).ToString();

        Assert.Equal("1", await service.ResolveAsync(TermKind.Actor, "Same", CancellationToken.None));
        Assert.Equal("2", await service.ResolveAsync(TermKind.Genre, "Same", CancellationToken.None));
        Assert.Equal(2, translator.Calls);
    }

    [Fact]
    public async Task ResolveAsync_EngineFailureKeepsOriginalAndRetriesNextTime()
    {
        var store = new FakeStore();
        var translator = new FakeTranslator { Error = new Exception("boom") };
        var service = new TermTranslationService(store, translator);

        var first = await service.ResolveAsync(TermKind.Genre, "単体作品", CancellationToken.None);
        var second = await service.ResolveAsync(TermKind.Genre, "単体作品", CancellationToken.None);

        Assert.Equal("単体作品", first);
        Assert.Equal("単体作品", second);
        // 失败不缓存也不写库，所以第二次还会重试引擎。
        Assert.Equal(2, translator.Calls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task ResolveAsync_RejectsLineBreaksWithoutSaving()
    {
        var store = new FakeStore();
        var translator = new FakeTranslator { Result = "第一行\n第二行" };
        var service = new TermTranslationService(store, translator);

        var result = await service.ResolveAsync(TermKind.Series, "シリーズ", CancellationToken.None);

        Assert.Equal("シリーズ", result);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task ResolveAsync_EmptyTranslationKeepsOriginalAndIsNotCached()
    {
        var store = new FakeStore();
        var translator = new FakeTranslator { Result = "   " };
        var service = new TermTranslationService(store, translator);

        var first = await service.ResolveAsync(TermKind.Genre, "S1", CancellationToken.None);
        var second = await service.ResolveAsync(TermKind.Genre, "S1", CancellationToken.None);

        Assert.Equal("S1", first);
        Assert.Equal("S1", second);
        Assert.Equal(2, translator.Calls);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task ResolveAsync_SameAsSourceIsCachedAsSuccess()
    {
        // 引擎成功但译名与原文相同（例如纯英文演员名），仍应写库并缓存。
        var store = new FakeStore();
        var translator = new FakeTranslator { Result = "Alice" };
        var service = new TermTranslationService(store, translator);

        await service.ResolveAsync(TermKind.Actor, "Alice", CancellationToken.None);
        await service.ResolveAsync(TermKind.Actor, "Alice", CancellationToken.None);

        Assert.Equal(1, translator.Calls);
        Assert.Equal(1, store.SaveCalls);
    }

    [Fact]
    public async Task ResolveAsync_StoreFailureDisablesDictionaryAfterOneWarning()
    {
        var store = new FakeStore { LookupError = new TranslationTermApiUnavailableException() };
        var translator = new FakeTranslator { Result = "译文" };
        var service = new TermTranslationService(store, translator);
        var warnings = new List<string>();
        Action<string> log = message =>
        {
            if (message.StartsWith("Translation dictionary unavailable"))
                warnings.Add(message);
        };

        await service.ResolveAsync(TermKind.Actor, "A", CancellationToken.None, log);
        await service.ResolveAsync(TermKind.Actor, "B", CancellationToken.None, log);

        // 字典不可用后不再查库，也不再尝试写入。
        Assert.Equal(1, store.LookupCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Single(warnings);
        Assert.Equal(2, translator.Calls);
    }

    [Fact]
    public async Task ResolveAsync_SaveFailureStillUsesLocalTranslation()
    {
        var store = new FakeStore { SaveError = new Exception("db down") };
        var translator = new FakeTranslator { Result = "译文" };
        var service = new TermTranslationService(store, translator);
        var warnings = 0;

        var result = await service.ResolveAsync(TermKind.Genre, "ジャンル", CancellationToken.None,
            _ => warnings++);

        Assert.Equal("译文", result);
        Assert.Equal(1, store.SaveCalls);
        Assert.Equal(1, warnings);
    }

    [Fact]
    public async Task ResolveAsync_BlankSourceIsReturnedAsIs()
    {
        var store = new FakeStore();
        var translator = new FakeTranslator();
        var service = new TermTranslationService(store, translator);

        var result = await service.ResolveAsync(TermKind.Actor, "   ", CancellationToken.None);

        Assert.Equal("   ", result);
        Assert.Equal(0, store.LookupCalls);
        Assert.Equal(0, translator.Calls);
    }

    private sealed class FakeStore : ITermStore
    {
        public string LookupResult { get; set; }
        public string SaveResult { get; set; }
        public Exception LookupError { get; set; }
        public Exception SaveError { get; set; }
        public int LookupCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public (string Source, string Translated) LastSaved { get; private set; }

        public Task<string> LookupAsync(TermKind kind, string source, CancellationToken cancellationToken)
        {
            LookupCalls++;
            return LookupError != null ? Task.FromException<string>(LookupError) : Task.FromResult(LookupResult);
        }

        public Task<string> SaveAsync(TermKind kind, string source, string translatedText,
            CancellationToken cancellationToken)
        {
            SaveCalls++;
            LastSaved = (source, translatedText);
            return SaveError != null ? Task.FromException<string>(SaveError) : Task.FromResult(SaveResult);
        }
    }

    private sealed class FakeTranslator : ITextTranslator
    {
        public string Result { get; set; }
        public Exception Error { get; set; }
        public int Calls { get; private set; }
        public Func<string, string> Translate { get; set; }

        public Task<string> TranslateAsync(string text, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error != null)
                return Task.FromException<string>(Error);

            return Task.FromResult(Translate != null ? Translate(text) : Result);
        }
    }
}
