using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员身份解析：别名表命中直接用；未命中走演员搜索 + 详情并回写；
/// A（B）里 A 直接当译名、B 用于搜索；表不可用时降级为搜索且只告警一次。
/// </summary>
public class ActorIdentityResolverTests
{
    [Fact]
    public async Task AliasHitUsesStoredIdentityWithoutSearching()
    {
        var store = new FakeStore();
        store.ByName["ゆい はつみ"] = Found("MINNANO", "7", "ゆい はつみ", "柚井初美");
        var directory = new FakeDirectory();
        var translator = new FakeTranslator("引擎译名");
        var resolver = new ActorIdentityResolver(store, directory, translator);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, true, "zh-CN",
            CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("柚井初美", actor.DisplayName);
        Assert.Equal("MINNANO:7", actor.IdentityKey);
        Assert.Equal(0, directory.SearchCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Equal(0, translator.Calls);
    }

    [Fact]
    public async Task MissSearchesActorAndSavesStageAndTranslation()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "42", Name = "柚井初美" }
            },
            Detail = new ActorInfo { Provider = "MINNANO", Id = "42", Name = "柚井初美" }
        };
        var translator = new FakeTranslator("引擎译名");
        var resolver = new ActorIdentityResolver(store, directory, translator);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, true, "zh-CN",
            CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("柚井初美", actor.DisplayName);
        Assert.Equal("MINNANO:42", actor.IdentityKey);
        Assert.Equal(0, translator.Calls);

        var saved = Assert.Single(store.Saved);
        Assert.Equal("ゆい はつみ", saved.StageName);
        Assert.Equal("柚井初美", saved.TranslatedName);
        Assert.Equal("source", saved.TranslationSource);
        Assert.Equal("MINNANO", saved.Provider);
        Assert.Equal("42", saved.ActorId);
        // 身份解析用一次批量查询，不再逐条查、也不再取演员详情。
        Assert.Equal(1, store.LookupManyCalls);
        Assert.Equal(0, store.LookupCalls);
        Assert.Equal(0, directory.GetCalls);
    }

    [Fact]
    public async Task NegativeCacheSkipsRepeatedMissesForSharedStageName()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        await resolver.ResolveAllAsync(new[] { "甲（ゆい はつみ）" }, false, "zh-CN", CancellationToken.None);
        await resolver.ResolveAllAsync(new[] { "乙（ゆい はつみ）" }, false, "zh-CN", CancellationToken.None);

        Assert.Equal(2, store.LookupManyCalls);
        Assert.DoesNotContain("ゆい はつみ", store.LastNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DifferentSpellingsOfSamePersonAreDeduplicated()
    {
        var store = new FakeStore();
        store.ByName["ゆい はつみ"] = Found("MINNANO", "7", "ゆい はつみ", "柚井初美");
        store.ByName["柚井初美"] = Found("MINNANO", "7", "ゆい はつみ", "柚井初美");
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
                new ActorAliasPersonInfo { Id = 1, StageName = "A", Provider = "MINNANO", ActorId = "1" },
                new ActorAliasPersonInfo { Id = 2, StageName = "B", Provider = "MINNANO", ActorId = "2" }
            }
        };
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var logs = new List<string>();
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN",
            CancellationToken.None, logs.Add);

        var actor = Assert.Single(actors);
        Assert.Equal("ゆい はつみ", actor.DisplayName);
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

        Assert.Equal(1, store.LookupManyCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Single(logs, message => message.StartsWith("Actor alias store unavailable"));
    }

    [Fact]
    public async Task TranslationCollisionFallsBackToOriginalDisplay()
    {
        var store = new FakeStore();
        store.ByName["あ"] = Found("MINNANO", "1", "甲", null);
        store.ByName["い"] = Found("MINNANO", "2", "乙", null);
        var resolver = new ActorIdentityResolver(store, new FakeDirectory(), new FakeTranslator("同名"));

        var actors = await resolver.ResolveAllAsync(new[] { "あ", "い" }, true, "zh-CN",
            CancellationToken.None);

        Assert.Equal(new[] { "同名", "乙" }, actors.Select(actor => actor.DisplayName));
    }

    [Fact]
    public async Task MissingActorKeepsSearchNameWithoutSaving()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "无名（むめい）" }, false, "zh-CN",
            CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("むめい", actor.DisplayName);
        Assert.Equal("むめい", actor.SearchName);
        Assert.False(actor.HasIdentity);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task ParsedChineseNameIsStoredAsTranslationWithoutEngineCall()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
            },
            Detail = new ActorInfo { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
        };
        var translator = new FakeTranslator("引擎译名");
        var resolver = new ActorIdentityResolver(store, directory, translator);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, true, "zh-CN",
            CancellationToken.None);

        Assert.Equal("柚井初美", Assert.Single(actors).DisplayName);
        Assert.Equal(0, translator.Calls);

        var saved = Assert.Single(store.Saved);
        Assert.Equal("ゆい はつみ", saved.StageName);
        Assert.Equal("柚井初美", saved.TranslatedName);
        Assert.Equal("source", saved.TranslationSource);
    }

    [Fact]
    public async Task ParsedChineseNameIsStoredEvenWhenTranslationOff()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
            },
            Detail = new ActorInfo { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
        };
        var translator = new FakeTranslator("引擎译名");
        var resolver = new ActorIdentityResolver(store, directory, translator);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN",
            CancellationToken.None);

        Assert.Equal("ゆい はつみ", Assert.Single(actors).DisplayName);
        Assert.Equal(0, translator.Calls);
        Assert.Equal("柚井初美", Assert.Single(store.Saved).TranslatedName);
    }

    [Fact]
    public async Task MissingParsedNameUsesEngineTranslation()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
            },
            Detail = new ActorInfo { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
        };
        var translator = new FakeTranslator("柚井初美");
        var resolver = new ActorIdentityResolver(store, directory, translator);

        var actors = await resolver.ResolveAllAsync(new[] { "ゆい はつみ" }, true, "zh-CN",
            CancellationToken.None);

        Assert.Equal("柚井初美", Assert.Single(actors).DisplayName);
        Assert.Equal(1, translator.Calls);
        var saved = Assert.Single(store.Saved);
        Assert.Equal("柚井初美", saved.TranslatedName);
        Assert.Equal("engine", saved.TranslationSource);
    }

    [Fact]
    public async Task PrefersRichActorProviderOverServerOrder()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "Gfriends", Id = "gf", Name = "柚井初美", Images = new[] { "http://img/gf.jpg" } },
                new() { Provider = "MINNANO", Id = "42", Name = "柚井初美" }
            }
        };
        directory.Details["Gfriends:gf"] = new ActorInfo { Provider = "Gfriends", Id = "gf", Name = "gf" };
        directory.Details["MINNANO:42"] = new ActorInfo { Provider = "MINNANO", Id = "42", Name = "柚井初美" };
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美" }, false, "zh-CN", CancellationToken.None);

        Assert.Equal("MINNANO:42", Assert.Single(actors).IdentityKey);
        Assert.Equal("MINNANO", Assert.Single(store.Saved).Provider);
    }

    [Fact]
    public async Task PrefersCjkNameWithinSameProvider()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "1", Name = "ゆい はつみ" },
                new() { Provider = "MINNANO", Id = "2", Name = "柚井初美" }
            }
        };
        directory.Details["MINNANO:1"] = new ActorInfo { Provider = "MINNANO", Id = "1", Name = "ゆい はつみ" };
        directory.Details["MINNANO:2"] = new ActorInfo { Provider = "MINNANO", Id = "2", Name = "柚井初美" };
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN",
            CancellationToken.None);

        Assert.Equal("MINNANO:2", Assert.Single(actors).IdentityKey);
    }

    [Fact]
    public async Task FallsBackToNextCandidateWhenNameDoesNotMatch()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "bad", Name = "别人" },
                new() { Provider = "AVBASE", Id = "good", Name = "柚井初美" }
            }
        };
        directory.Details["MINNANO:bad"] = new ActorInfo { Provider = "MINNANO", Id = "bad", Name = "别人" };
        directory.Details["AVBASE:good"] = new ActorInfo
        {
            Provider = "AVBASE", Id = "good", Name = "柚井初美", Aliases = new[] { "ゆい はつみ" }
        };
        var resolver = new ActorIdentityResolver(store, directory, new FakeTranslator());

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN",
            CancellationToken.None);

        Assert.Equal("AVBASE:good", Assert.Single(actors).IdentityKey);
    }

    [Fact]
    public async Task ResolutionIsCachedByRawName()
    {
        var store = new FakeStore();
        store.ByName["柚井初美"] = Found("MINNANO", "7", "柚井初美", "柚井初美");
        var resolver = new ActorIdentityResolver(store, new FakeDirectory(), new FakeTranslator());

        await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN", CancellationToken.None);
        var lookups = store.LookupManyCalls;
        await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, false, "zh-CN", CancellationToken.None);

        Assert.Equal(lookups, store.LookupManyCalls);
    }

    private static ActorAliasResult Found(string provider, string id, string stage, string translated)
    {
        return new ActorAliasResult
        {
            Found = true,
            Person = new ActorAliasPersonInfo
            {
                Id = uint.Parse(id),
                StageName = stage,
                TranslatedName = translated,
                Provider = provider,
                ActorId = id
            }
        };
    }

    private sealed class FakeStore : IActorAliasStore
    {
        public Dictionary<string, ActorAliasResult> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception LookupError { get; set; }
        public int LookupCalls { get; private set; }
        public int SaveCalls { get; private set; }
        public List<ActorAliasRequest> Saved { get; } = new();

        public int LookupManyCalls { get; private set; }
        public List<string> LastNames { get; private set; } = new();

        public Task<ActorAliasResult> LookupAsync(string name, CancellationToken cancellationToken)
        {
            LookupCalls++;
            if (LookupError != null)
                return Task.FromException<ActorAliasResult>(LookupError);
            return Task.FromResult(ByName.TryGetValue(name, out var result)
                ? result
                : new ActorAliasResult { Found = false });
        }

        public Task<IReadOnlyDictionary<string, ActorAliasResult>> LookupManyAsync(
            IReadOnlyCollection<string> names, CancellationToken cancellationToken)
        {
            LookupManyCalls++;
            LastNames = names.ToList();
            if (LookupError != null)
                return Task.FromException<IReadOnlyDictionary<string, ActorAliasResult>>(LookupError);

            var map = new Dictionary<string, ActorAliasResult>(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                map[name] = ByName.TryGetValue(name, out var result)
                    ? result
                    : new ActorAliasResult { Found = false };
            return Task.FromResult<IReadOnlyDictionary<string, ActorAliasResult>>(map);
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
                    StageName = request.StageName,
                    TranslatedName = request.TranslatedName,
                    Provider = request.Provider,
                    ActorId = request.ActorId
                }
            });
        }
    }

    private sealed class FakeDirectory : IActorDirectory
    {
        public List<ActorSearchResult> SearchResults { get; set; } = new();
        public ActorInfo Detail { get; set; }
        public Dictionary<string, ActorInfo> Details { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int SearchCalls { get; private set; }
        public int GetCalls { get; private set; }

        public Task<List<ActorSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
        {
            SearchCalls++;
            return Task.FromResult(SearchResults);
        }

        public Task<ActorInfo> GetAsync(string provider, string id, CancellationToken cancellationToken)
        {
            GetCalls++;
            if (Details.TryGetValue(provider + ":" + id, out var detail))
                return Task.FromResult(detail);
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
