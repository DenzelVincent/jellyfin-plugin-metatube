using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员身份解析（艺名版）：查表命中取详情组候选池回写；未命中走搜索+详情组池回写；
/// 显示名一律用服务端返回的艺名，插件不做汉字评分、不再翻译演员名。
/// </summary>
public class ActorIdentityResolverTests
{
    [Fact]
    public async Task AliasHitFetchesDetailAndSavesCandidatePool()
    {
        var store = new FakeStore { SaveResultStage = "服务端优选名" };
        store.ByName["ゆい はつみ"] = Found("MINNANO", "7", "ゆい はつみ");
        var directory = new FakeDirectory
        {
            Detail = new ActorInfo
            {
                Provider = "MINNANO", Id = "7", Name = "柚井初美",
                Aliases = new[] { "ゆい はつみ", "Yui Hatsumi" }
            }
        };
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "汉字别名（ゆい はつみ）" }, CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("服务端优选名", actor.Name);
        Assert.Equal("MINNANO:7", actor.IdentityKey);
        Assert.Equal(0, directory.SearchCalls);
        Assert.Equal(1, directory.GetCalls);
        Assert.Equal(1, store.LookupManyCalls);

        // 候选池顺序：详情 Name → 详情 Aliases → A；重复写法去重，评分交给服务端。
        var saved = Assert.Single(store.Saved);
        Assert.Equal(new[] { "柚井初美", "ゆい はつみ", "Yui Hatsumi", "汉字别名" }, saved.Names);
        Assert.Equal("MINNANO", saved.Provider);
        Assert.Equal("7", saved.ActorId);
        Assert.Equal("auto", saved.Source);
    }

    [Fact]
    public async Task AliasHitDetailFailureUsesStoredStageWithoutPut()
    {
        var store = new FakeStore();
        store.ByName["ゆい はつみ"] = Found("MINNANO", "7", "ゆい はつみ");
        var directory = new FakeDirectory { GetError = new Exception("detail down") };
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("ゆい はつみ", actor.Name);
        Assert.Equal("MINNANO:7", actor.IdentityKey);
        Assert.Equal(0, store.SaveCalls);
    }

    [Fact]
    public async Task MissSearchesAndSavesPoolWithDetailAndSearchNames()
    {
        var store = new FakeStore { SaveResultStage = "柚井初美" };
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
            },
            Detail = new ActorInfo
            {
                Provider = "MINNANO", Id = "42", Name = "柚井初美",
                Aliases = new[] { "Yui Hatsumi" }
            }
        };
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("柚井初美", actor.Name);
        Assert.Equal("MINNANO:42", actor.IdentityKey);

        // 候选池：详情 Name → Aliases → A → 搜索结果名（按来源质量排序）。
        var saved = Assert.Single(store.Saved);
        Assert.Equal(new[] { "柚井初美", "Yui Hatsumi", "ゆい はつみ" }, saved.Names);
        Assert.Equal("auto", saved.Source);
        Assert.Equal(1, directory.SearchCalls);
        Assert.Equal(1, directory.GetCalls);
    }

    [Fact]
    public async Task MissDetailFailureStillSavesSearchPool()
    {
        var store = new FakeStore { SaveResultStage = "柚井初美" };
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "42", Name = "ゆい はつみ" }
            },
            GetError = new Exception("detail down")
        };
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, CancellationToken.None);

        Assert.Equal("柚井初美", Assert.Single(actors).Name);
        var saved = Assert.Single(store.Saved);
        Assert.Equal(new[] { "柚井初美", "ゆい はつみ" }, saved.Names);
    }

    [Fact]
    public async Task PutFailureFallsBackToDetailNameAndDisablesStore()
    {
        var store = new FakeStore { SaveError = new Exception("put down") };
        var directory = new FakeDirectory
        {
            SearchResults = new List<ActorSearchResult>
            {
                new() { Provider = "MINNANO", Id = "1", Name = "ゆい はつみ" },
                new() { Provider = "AVBASE", Id = "2", Name = "あおい" }
            },
            Detail = new ActorInfo { Provider = "MINNANO", Id = "1", Name = "柚井初美" }
        };
        var logs = new List<string>();
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "ゆい はつみ", "あおい" },
            CancellationToken.None, logs.Add);

        Assert.Equal("柚井初美", actors[0].Name);
        Assert.Equal(1, store.SaveCalls);
        Assert.Single(logs, message => message.StartsWith("Actor alias store unavailable"));
    }

    [Fact]
    public async Task NoSearchMatchKeepsOriginalNameWithoutSave()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "无名（むめい）" }, CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("むめい", actor.Name);
        Assert.Equal("むめい", actor.SearchName);
        Assert.False(actor.HasIdentity);
        Assert.Equal(0, store.SaveCalls);
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
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" },
            CancellationToken.None, logs.Add);

        var actor = Assert.Single(actors);
        Assert.Equal("ゆい はつみ", actor.Name);
        Assert.False(actor.HasIdentity);
        Assert.Equal(0, store.SaveCalls);
        Assert.Contains(logs, message => message.StartsWith("Actor alias is ambiguous"));
    }

    [Fact]
    public async Task SamePidDeduplicatedAcrossSpellings()
    {
        var store = new FakeStore();
        store.ByName["ゆい はつみ"] = Found("MINNANO", "7", "ゆい はつみ");
        store.ByName["柚井初美"] = Found("MINNANO", "7", "ゆい はつみ");
        var resolver = new ActorIdentityResolver(store, new FakeDirectory
        {
            Detail = new ActorInfo { Provider = "MINNANO", Id = "7", Name = "柚井初美" }
        });

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）", "柚井初美" },
            CancellationToken.None);

        Assert.Single(actors);
    }

    [Fact]
    public async Task ResolutionCachedByRawName()
    {
        var store = new FakeStore();
        store.ByName["柚井初美"] = Found("MINNANO", "7", "柚井初美");
        var resolver = new ActorIdentityResolver(store, new FakeDirectory
        {
            Detail = new ActorInfo { Provider = "MINNANO", Id = "7", Name = "柚井初美" }
        });

        await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, CancellationToken.None);
        var lookups = store.LookupManyCalls;
        var saves = store.SaveCalls;
        await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, CancellationToken.None);

        Assert.Equal(lookups, store.LookupManyCalls);
        Assert.Equal(saves, store.SaveCalls);
    }

    [Fact]
    public async Task NegativeCacheSkipsRepeatedMissesForSharedSearchName()
    {
        var store = new FakeStore();
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var resolver = new ActorIdentityResolver(store, directory);

        await resolver.ResolveAllAsync(new[] { "甲（ゆい はつみ）" }, CancellationToken.None);
        await resolver.ResolveAllAsync(new[] { "乙（ゆい はつみ）" }, CancellationToken.None);

        Assert.Equal(2, store.LookupManyCalls);
        Assert.DoesNotContain("ゆい はつみ", store.LastNames, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StoreFailureDisablesDictionaryAfterOneWarning()
    {
        var store = new FakeStore { LookupError = new Exception("network down") };
        var directory = new FakeDirectory { SearchResults = new List<ActorSearchResult>() };
        var logs = new List<string>();
        var resolver = new ActorIdentityResolver(store, directory);

        await resolver.ResolveAllAsync(new[] { "甲（あ）", "乙（い）" }, CancellationToken.None, logs.Add);

        Assert.Equal(1, store.LookupManyCalls);
        Assert.Equal(0, store.SaveCalls);
        Assert.Single(logs, message => message.StartsWith("Actor alias store unavailable"));
        // 降级后仍走演员搜索，刮削不中断。
        Assert.True(directory.SearchCalls > 0);
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
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美" }, CancellationToken.None);

        var actor = Assert.Single(actors);
        Assert.Equal("MINNANO:42", actor.IdentityKey);
        Assert.Equal("MINNANO", Assert.Single(store.Saved).Provider);
        Assert.NotNull(actor.GfriendsImageSource);
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
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, CancellationToken.None);

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
        var resolver = new ActorIdentityResolver(store, directory);

        var actors = await resolver.ResolveAllAsync(new[] { "柚井初美（ゆい はつみ）" }, CancellationToken.None);

        Assert.Equal("AVBASE:good", Assert.Single(actors).IdentityKey);
    }

    [Fact]
    public async Task CancellationPropagatesFromLookup()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var store = new FakeStore { LookupError = new OperationCanceledException(cts.Token) };
        var resolver = new ActorIdentityResolver(store, new FakeDirectory());

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            resolver.ResolveAllAsync(new[] { "柚井初美" }, cts.Token));
    }

    private static ActorAliasResult Found(string provider, string id, string stage)
    {
        return new ActorAliasResult
        {
            Found = true,
            Person = new ActorAliasPersonInfo
            {
                Id = uint.Parse(id),
                StageName = stage,
                Provider = provider,
                ActorId = id
            }
        };
    }

    private sealed class FakeStore : IActorAliasStore
    {
        public Dictionary<string, ActorAliasResult> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception LookupError { get; set; }
        public Exception SaveError { get; set; }
        public string SaveResultStage { get; set; }
        public int SaveCalls { get; private set; }
        public List<ActorAliasRequest> Saved { get; } = new();
        public int LookupManyCalls { get; private set; }
        public List<string> LastNames { get; private set; } = new();

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
            if (SaveError != null)
                return Task.FromException<ActorAliasResult>(SaveError);
            return Task.FromResult(new ActorAliasResult
            {
                Found = true,
                Person = new ActorAliasPersonInfo
                {
                    Id = 1,
                    StageName = SaveResultStage ?? request.Names?.FirstOrDefault(),
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
        public Exception GetError { get; set; }
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
            if (GetError != null)
                return Task.FromException<ActorInfo>(GetError);
            if (Details.TryGetValue(provider + ":" + id, out var detail))
                return Task.FromResult(detail);
            return Task.FromResult(Detail);
        }
    }
}
