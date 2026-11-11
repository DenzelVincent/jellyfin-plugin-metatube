using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员身份解析：别名表命中直接用；未命中走演员搜索 + 详情并回写；
/// 歧义不自动合并；表不可用时降级为搜索且只告警一次；同一 pid 去重。
/// </summary>
public class ActorIdentityResolverTests
{
    [Fact]
    public async Task AliasHitUsesStoredIdentityWithoutSearching()
    {
        var store = new FakeStore();
        store.ByName["柚井初美"] = Found("MINNANO", "7", "柚井初美", "柚井初美");
        var directory = new FakeDirectory();
        var translator = new FakeTranslator("柚井初美");
        var resolver = new ActorIdentityResolver(store, directory, translator);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, true, "zh-CN",
            CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("柚井初美", actor.DisplayName);
        Assert.True(actor.HasIdentity);
        Assert.Equal("MINNANO:7", actor.IdentityKey);
        Assert.Equal(0, directory.SearchCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, translator.Calls);
    }

    [Fact]
    public async Task MissSearchesActorAndSavesAllNameForms()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "42", Name = "柚井初美" }
            },
            Detail = new ActorInfo
            {
                Provider = "MINNANO",
                Id = "42",
                Name = "柚井初美",
                Aliases = new[] { "ゆい はつみ", "Yui Hatsumi" },
                Images = new[] { "http://img/1.jpg" }
            }
        };
        var translator = new FakeTranslator("柚井初美");
        var resolver = new ActorIdentityResolver(store, directory, translator);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, true, "zh-CN",
            CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("柚井初美", actor.DisplayName);
        Assert.Equal("MINNANO:42", actor.IdentityKey);

        var saved = Assert.Single(store.Saved);
        Assert.Equal("MINNANO", saved.Provider);
        Assert.Equal("42", saved.ActorId);
        Assert.Equal("柚井初美（ゆい はつみ）", saved.Names.Single(n => n.Role == "alias").Name);
        Assert.Contains(saved.Names, n => n.Name == "柚井初美" && n.Role == "stage");
        Assert.Contains(saved.Names, n => n.Name == "ゆい はつみ" && n.Role == "kana");
        Assert.Contains(saved.Names, n => n.Name == "Yui Hatsumi" && n.Role == "romaji");
        Assert.NotNull(saved.Translation);
        Assert.Equal("zh-CN", saved.Translation.Lang);
        Assert.Equal("柚井初美", saved.Translation.Text);
    }

    [Fact]
    public async Task DifferentSpellingsOfSamePersonAreDeduplicated()
    {
        var store = new FakeStore();
        store.ByName["ゆい はつみ"] = Found("MINNANO", "7", "柚井初美", null);
        store.ByName["柚井初美"] = Found("MINNANO", "7", "柚井初美", null);
        var resolver = new ActorIdentityResolver(store, new FakeDirectory(), new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）", "柚井初美" }, false,
            "zh-CN", CancellationToken.None);

        Assert.Single(actors);
    }

    [Fact]
    public async Task AmbiguousAliasFallsBackToSearchWithoutSaving()
    {
        var store = new FakeStore();
        store.ByName["柚井初美"] = new ActorAliasResult
        {
            Found = true,
            Ambiguous = true,
            Matches = new[]
            {
                new ActorAliasPersonInfo { Id = 1, DisplayName = "A", Provider = "MINNANO", ActorId = "1" },
                new ActorAliasPersonInfo { Id = 2, DisplayName = "B", Provider = "MINNANO", ActorId = "2" }
            }
        };
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var logs = new List<string>();
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN",
            CancellationToken.None, logs.Add);

        var actor = Assert.Single(actors);
        Assert.Equal("柚井初美", actor.DisplayName);
        Assert.False(actor.HasIdentity);
        Assert.Equal(0, store.SaveCalls);
        Assert.Contains(logs, message => message.StartsWith("Actor alias is ambiguous"));
    }

    [Fact]
    public async Task StoreFailureDisablesDictionaryAfterOneWarning()
    {
        var store = new FakeStore { LookupError = new ActorAliasApiUnavailableException() };
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var logs = new List<string>();
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        await resolver.ResolveAllAsync(new[] { "甲（あ）", "乙（い）" }, false, "zh-CN",
            CancellationToken.None, logs.Add);

        Assert.Equal(1, store.LookupCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Single(logs, message => message.StartsWith("Actor alias store unavailable"));
    }

    [Fact]
    public async Task TranslationCollisionFallsBackToOriginalDisplay()
    {
        var store = new FakeStore();
        store.ByName["あ"] = Found("MINNANO", "1", "甲", null);
        store.ByName["い"] = Found("MINNANO", "2", "乙", null);
        var translator = new FakeTranslator("同名");
        var resolver = new ActorIdentityResolver(store, new FakeDirectory(), translator);

        var actors = await resolver.ResolveAllAsync(new[] { "甲（あ）", "乙（い）" }, true, "zh-CN",
            CancellationToken.None);

        Assert.Equal(new[] { "同名", "乙" }, actors.Select(actor => actor.DisplayName));
    }

    [Fact]
    public async Task MissingActorKeepsParsedDisplayWithoutSaving()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "无名（むめい）" }, false, "zh-CN",
            CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("无名", actor.DisplayName);
        Assert.Equal("むめい", actor.SearchName);
        Assert.False(actor.HasIdentity);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task ResolutionIsCachedByRawName()
    {
        var store = new FakeStore();
        store.ByName["柚井初美"] = Found("MINNANO", "7", "柚井初美", null);
        var resolver = new ActorIdentityResolver(store, new FakeDirectory(), new FakeTranslator());

        await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN", CancellationToken.None);
        var lookups = store.LookupCalls;
        await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN", CancellationToken.None);

        Assert.Equal(lookups, store.LookupCalls);
    }

    private static ActorAliasResult Found(string provider, string id, string display, string translated)
    {
        return new ActorAliasResult
        {
            Found = true,
            Person = new ActorAliasPersonInfo
            {
                Id = uint.Parse(id),
                DisplayName = display,
                Provider = provider,
                ActorId = id
            },
            TranslatedName = translated
        };
    }

    private sealed class FakeStore : IActorAliasStore
    {
        public Dictionary<string, ActorAliasResult> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception LookupError { get; set; }
        public int LookupCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public List<ActorAliasRequest> Saved { get; } = new();

        public Task<ActorAliasResult> LookupAsync(string name, string targetLang, CancellationToken cancellationToken)
        {
            LookupCalls++;
            if (LookupError != null)
                return Task.FromException<ActorAliasResult>(LookupError);
            return Task.FromResult(ByName.TryGetValue(name, out var result)
                ? result
                : new ActorAliasResult { Found = false });
        }

        public Task<ActorAliasResult> SaveAsync(ActorAliasRequest request, CancellationToken cancellationToken)
        {
            SaveCalls++;
            Saved.Add(request);
            return Task.FromResult(new ActorAliasResult
            {
                Found = true,
                Person = new ActorAliasPersonInfo
                {
                    Id = 1,
                    DisplayName = request.DisplayName,
                    Provider = request.Provider,
                    ActorId = request.ActorId
                },
                Names = request.Names,
                TranslatedName = request.Translation?.Text
            });
        }
    }

    private sealed class FakeDirectory : IActorDirectory
    {
        public List<ActorSearchResult> SearchResults { get; set; } = new();
        public ActorInfo Detail { get; set; }
        public int SearchCalls { get; private set; }

        public Task<List<ActorSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return Task.FromResult(SearchResults);
        }

        public Task<ActorInfo> GetAsync(string provider, string id, CancellationToken cancellationToken)
        {
            return Task.FromResult(Detail);
        }
    }

    private sealed class FakeTranslator : IActorNameTranslator
    {
        private readonly string _result;

        public FakeTranslator(string result = null)
        {
            _result = result;
        }

        public int Calls { get; private set; }

        public Task<string> TranslateAsync(string name, CancellationToken cancellationToken, Action<string> log)
        {
            Calls++;
            return Task.FromResult(_result ?? name);
        }
    }
}
