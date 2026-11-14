using System.Collections.Concurrent;
using System.Text;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

/// <summary>头像来源：provider + id + 原始图片地址（URL 由调用方按服务端规则拼）。</summary>
public sealed class ActorImageSource
{
    public string Provider { get; set; }

    public string Id { get; set; }

    public string Path { get; set; }
}

/// <summary>一次解析得到的演员：艺名（服务端优选）、搜索名与来源 pid。</summary>
public sealed class ResolvedActor
{
    public string RawName { get; set; }

    /// <summary>最终显示名：服务端返回的艺名；表不可用时退化为详情规范名或候选池首个。</summary>
    public string Name { get; set; }

    /// <summary>搜索用名（A（B）里的 B）；无身份时头像阶段拿它再搜一次。</summary>
    public string SearchName { get; set; }

    public string Provider { get; set; }

    public string ActorId { get; set; }

    /// <summary>搜索阶段拿到的首选头像；详情接口的图会先应用、它最后覆盖。</summary>
    public ActorImageSource ImageSource { get; set; }

    /// <summary>Gfriends 头像兜底（搜索结果里带图时才有）。</summary>
    public ActorImageSource GfriendsImageSource { get; set; }

    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(ActorId);

    public string IdentityKey => HasIdentity
        ? Provider + ":" + ActorId
        : "name:" + (Name ?? string.Empty).ToLowerInvariant();
}

/// <summary>服务端演员别名表的读写入口。失败时抛异常，由调用方决定是否降级。</summary>
public interface IActorAliasStore
{
    /// <summary>批量查询；返回按请求名索引的结果，未出现的名字视为未命中。</summary>
    Task<IReadOnlyDictionary<string, ActorAliasResult>> LookupManyAsync(IReadOnlyCollection<string> names,
        CancellationToken cancellationToken);

    Task<ActorAliasResult> SaveAsync(ActorAliasRequest request, CancellationToken cancellationToken);
}

/// <summary>演员搜索与详情入口。</summary>
public interface IActorDirectory
{
    Task<List<ActorSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<ActorInfo> GetAsync(string provider, string id, CancellationToken cancellationToken);
}

/// <summary>
/// 演员身份解析：名字拆分 → 别名表 → 演员搜索/详情 → 组候选池回写 → 用服务端艺名。
/// 汉字优选在服务端，插件只收集与排序候选名；别名表不可用时进程内降级为
/// 「只按演员搜索 + 详情」，不打断刮削。
/// </summary>
public sealed class ActorIdentityResolver
{
    private readonly IActorAliasStore _store;
    private readonly IActorDirectory _directory;
    private readonly ConcurrentDictionary<string, ResolvedActor> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    // 查不到的名字短期记为负命中，避免同一演员在不同影片里反复空查。
    private readonly ConcurrentDictionary<string, DateTime> _negative = new(StringComparer.Ordinal);
    private static readonly TimeSpan NegativeTtl = TimeSpan.FromMinutes(15);
    private int _storeDisabled;

    public ActorIdentityResolver(IActorAliasStore store, IActorDirectory directory)
    {
        _store = store;
        _directory = directory;
    }

    /// <summary>
    /// 解析一部影片的演员列表。同一个 pid 的多种写法只保留一位。
    /// </summary>
    public async Task<List<ResolvedActor>> ResolveAllAsync(IEnumerable<string> rawNames,
        CancellationToken cancellationToken, Action<string> log = null)
    {
        log ??= _ => { };
        var actors = new List<ResolvedActor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 先把整部片还没缓存的演员名批量查一遍，后续解析直接用结果。
        var pending = new List<string>();
        var pendingSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in rawNames ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            var key = raw.Trim();
            if (_cache.ContainsKey(key) || !pendingSeen.Add(key))
                continue;
            pending.Add(key);
        }
        var prefetched = await PrefetchAsync(pending, cancellationToken, log).ConfigureAwait(false);

        foreach (var raw in rawNames ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var actor = await ResolveOneAsync(raw, prefetched, cancellationToken, log).ConfigureAwait(false);
            if (actor == null || !seen.Add(actor.IdentityKey))
                continue;
            actors.Add(actor);
        }

        return actors;
    }

    private async Task<IReadOnlyDictionary<string, ActorAliasResult>> PrefetchAsync(List<string> raws,
        CancellationToken cancellationToken, Action<string> log)
    {
        if (raws.Count == 0 || IsStoreDisabled)
            return null;

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in raws)
        {
            var parts = ActorNameHelper.Parse(raw);
            var stage = !string.IsNullOrWhiteSpace(parts.SearchName) ? parts.SearchName.Trim() : raw;
            if (!IsNegative(stage))
                names.Add(stage);
            if (!string.IsNullOrWhiteSpace(parts.DisplayName))
            {
                var surface = parts.DisplayName.Trim();
                if (!string.Equals(surface, stage, StringComparison.Ordinal) && !IsNegative(surface))
                    names.Add(surface);
            }
        }
        if (names.Count == 0)
            return null;

        try
        {
            var result = await _store.LookupManyAsync(names.ToList(), cancellationToken).ConfigureAwait(false);
            if (result != null)
            {
                foreach (var name in names)
                {
                    if (result.TryGetValue(name, out var item) && item?.Found == true)
                        ClearNegative(name);
                    else
                        RememberNegative(name);
                }
            }
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            DisableStore(e, log);
            return null;
        }
    }

    private async Task<ResolvedActor> ResolveOneAsync(string raw,
        IReadOnlyDictionary<string, ActorAliasResult> prefetched, CancellationToken cancellationToken,
        Action<string> log)
    {
        var key = raw.Trim();
        if (_cache.TryGetValue(key, out var cached))
            return Clone(cached);

        var resolved = await ResolveIdentityAsync(key, prefetched, cancellationToken, log).ConfigureAwait(false);
        if (resolved == null)
            return null;
        _cache[key] = resolved;
        return Clone(resolved);
    }

    private async Task<ResolvedActor> ResolveIdentityAsync(string raw,
        IReadOnlyDictionary<string, ActorAliasResult> prefetched, CancellationToken cancellationToken,
        Action<string> log)
    {
        var parts = ActorNameHelper.Parse(raw);
        // B：搜索用的艺名（含假名写法）；A：影片来源可能已转换好的汉字名，进候选池。
        var stage = !string.IsNullOrWhiteSpace(parts.SearchName) ? parts.SearchName.Trim() : raw.Trim();
        var surface = string.IsNullOrWhiteSpace(parts.DisplayName) ? stage : parts.DisplayName.Trim();

        var lookup = await LookupAsync(surface, stage, prefetched, log).ConfigureAwait(false);
        if (lookup?.Found == true && !lookup.Ambiguous && lookup.Person != null)
            return await ResolveFromTableAsync(raw, stage, surface, lookup.Person, cancellationToken, log)
                .ConfigureAwait(false);

        // 表未命中（或歧义）：走演员搜索，把候选池学进表。
        var match = await SearchBestAsync(stage, surface, cancellationToken, log).ConfigureAwait(false);
        if (match == null)
        {
            log($"Actor not found: {stage}, keep original name");
            return new ResolvedActor
            {
                RawName = raw,
                Name = stage,
                SearchName = stage
            };
        }

        var result = match.Result;
        var actor = new ResolvedActor
        {
            RawName = raw,
            Name = stage,
            SearchName = stage,
            Provider = result.Provider,
            ActorId = result.Id,
            ImageSource = match.Image,
            GfriendsImageSource = match.GfriendsImage
        };

        var detail = await TryGetDetailAsync(result.Provider, result.Id, cancellationToken, log)
            .ConfigureAwait(false);
        var pool = BuildCandidatePool(detail, surface, match.RankedResults.Select(item => item.Name));
        var applied = await SavePoolAsync(actor, pool, cancellationToken, log).ConfigureAwait(false);
        if (!applied)
            actor.Name = pool.Length > 0 ? pool[0] : stage;
        ClearNegative(stage);
        ClearNegative(surface);
        return actor;
    }

    /// <summary>查表命中：取详情组池回写，用服务端艺名；详情失败时直接用表里艺名、不回写。</summary>
    private async Task<ResolvedActor> ResolveFromTableAsync(string raw, string stage, string surface,
        ActorAliasPersonInfo person, CancellationToken cancellationToken, Action<string> log)
    {
        var storedStage = string.IsNullOrWhiteSpace(person.StageName) ? stage : person.StageName.Trim();
        var actor = new ResolvedActor
        {
            RawName = raw,
            Name = storedStage,
            SearchName = stage,
            Provider = person.Provider,
            ActorId = person.ActorId
        };

        var detail = await TryGetDetailAsync(person.Provider, person.ActorId, cancellationToken, log)
            .ConfigureAwait(false);
        if (detail != null)
        {
            var pool = BuildCandidatePool(detail, surface, null);
            await SavePoolAsync(actor, pool, cancellationToken, log).ConfigureAwait(false);
        }

        ClearNegative(stage);
        ClearNegative(surface);
        ClearNegative(actor.Name);
        return actor;
    }

    /// <summary>回写候选池；成功时用服务端返回的艺名与身份覆盖并返回 true。失败只降级，不抛。</summary>
    private async Task<bool> SavePoolAsync(ResolvedActor actor, string[] pool, CancellationToken cancellationToken,
        Action<string> log)
    {
        if (!actor.HasIdentity || IsStoreDisabled || pool.Length == 0)
            return false;

        var saved = await SaveAsync(new ActorAliasRequest
        {
            Names = pool,
            Provider = actor.Provider,
            ActorId = actor.ActorId,
            Source = "auto"
        }, cancellationToken, log).ConfigureAwait(false);

        if (saved?.Found != true || saved.Person == null)
            return false;
        if (!string.IsNullOrWhiteSpace(saved.Person.StageName))
            actor.Name = saved.Person.StageName.Trim();
        actor.Provider = saved.Person.Provider;
        actor.ActorId = saved.Person.ActorId;
        return true;
    }

    private async Task<ActorAliasResult> LookupAsync(string display, string search,
        IReadOnlyDictionary<string, ActorAliasResult> prefetched, Action<string> log)
    {
        if (IsStoreDisabled || prefetched == null)
            return null;

        // B 优先；A 只在 B 未命中且与 B 不同时查。原始串不入表，不再作为候选。
        var candidates = new[] { search, display }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var candidate in candidates)
        {
            if (!prefetched.TryGetValue(candidate, out var result) || result?.Found != true)
                continue;
            if (result.Ambiguous)
                log($"Actor alias is ambiguous: {candidate}");
            return result;
        }
        return null;
    }

    // 身份来源按演员数据质量排序：MINNANO 有汉字/假名/拼音/别名，AVBASE 有真实姓名，
    // Gfriends 只有图片（详情里的图服务端会另行注入），所以只作最后兜底。
    private const string Gfriends = "Gfriends";

    private static readonly string[] ActorProviderPreference =
    {
        "MINNANO", "AVBASE", "AV-LEAGUE", "ThePornDB", Gfriends, "Wikipedia"
    };

    private const int MaxCandidateChecks = 3;

    private sealed class ActorSearchMatch
    {
        public ActorSearchResult Result { get; set; }

        public ActorImageSource Image { get; set; }

        public ActorImageSource GfriendsImage { get; set; }

        /// <summary>命中那一轮搜索的全部结果（按来源质量排序），名字进候选池。</summary>
        public List<ActorSearchResult> RankedResults { get; set; }
    }

    /// <summary>
    /// 在多个来源的搜索结果里挑身份：按数据质量排序，优先汉字名，并用搜索结果的
    /// name 与查询名做匹配校验；都校验不上时退回排序第一的候选并记日志。
    /// </summary>
    private async Task<ActorSearchMatch> SearchBestAsync(
        string search, string display, CancellationToken cancellationToken, Action<string> log)
    {
        var queries = new[] { search, display }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (var query in queries)
        {
            List<ActorSearchResult> results;
            try
            {
                results = await _directory.SearchAsync(query, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                log($"Search actor failed: {query} ({e.Message})");
                continue;
            }

            if (results?.Any() != true)
                continue;

            var ranked = results
                .Select((result, index) => new { Result = result, Index = index })
                .OrderBy(item => ProviderRank(item.Result.Provider))
                .ThenByDescending(item => ContainsCjk(item.Result.Name))
                .ThenBy(item => item.Index)
                .Select(item => item.Result)
                .ToList();

            var gfriends = results.FirstOrDefault(result =>
                string.Equals(result.Provider, Gfriends, StringComparison.OrdinalIgnoreCase) &&
                result.Images?.Any() == true);
            var gfriendsImage = ToImageSource(gfriends);

            ActorSearchResult fallbackResult = null;
            foreach (var candidate in ranked.Take(MaxCandidateChecks))
            {
                fallbackResult ??= candidate;
                if (queries.Any(name => MatchesName(candidate.Name, name)))
                {
                    return new ActorSearchMatch
                    {
                        Result = candidate,
                        Image = ToImageSource(candidate),
                        GfriendsImage = gfriendsImage,
                        RankedResults = ranked
                    };
                }
            }

            if (fallbackResult != null)
            {
                log($"No verified actor match: {query}, use {fallbackResult.Provider}:{fallbackResult.Id}");
                return new ActorSearchMatch
                {
                    Result = fallbackResult,
                    Image = ToImageSource(fallbackResult),
                    GfriendsImage = gfriendsImage,
                    RankedResults = ranked
                };
            }
        }

        return null;
    }

    /// <summary>
    /// 候选池固定顺序：详情 Name → 详情 Aliases → A → 搜索结果名。
    /// 只做非空过滤与同写法去重，汉字评分交给服务端。
    /// </summary>
    private static string[] BuildCandidatePool(ActorInfo detail, string surface, IEnumerable<string> searchNames)
    {
        var pool = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string name)
        {
            var value = name?.Trim();
            if (!string.IsNullOrEmpty(value) && seen.Add(value))
                pool.Add(value);
        }

        if (detail != null)
        {
            Add(detail.Name);
            if (detail.Aliases != null)
            {
                foreach (var alias in detail.Aliases)
                    Add(alias);
            }
        }
        Add(surface);
        if (searchNames != null)
        {
            foreach (var name in searchNames)
                Add(name);
        }
        return pool.ToArray();
    }

    private async Task<ActorInfo> TryGetDetailAsync(string provider, string id,
        CancellationToken cancellationToken, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(id))
            return null;
        try
        {
            return await _directory.GetAsync(provider, id, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log($"Get actor detail failed: {provider}:{id} ({e.Message})");
            return null;
        }
    }

    private static int ProviderRank(string provider)
    {
        for (var i = 0; i < ActorProviderPreference.Length; i++)
        {
            if (string.Equals(ActorProviderPreference[i], provider, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return ActorProviderPreference.Length;
    }

    private static bool ContainsCjk(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        foreach (var c in name)
        {
            if ((c >= '\u3400' && c <= '\u9fff') || (c >= '\uf900' && c <= '\ufaff'))
                return true;
        }
        return false;
    }

    private static bool MatchesName(string name, string query)
    {
        var key = NormalizeName(query);
        return key.Length > 0 && NormalizeName(name) == key;
    }

    private static string NormalizeName(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;

        var normalized = name.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (!char.IsWhiteSpace(c))
                builder.Append(char.ToLowerInvariant(c));
        }
        return builder.ToString();
    }

    private static ActorImageSource ToImageSource(ActorSearchResult result)
    {
        if (result?.Images?.Any() != true)
            return null;
        return new ActorImageSource
        {
            Provider = result.Provider,
            Id = result.Id,
            Path = result.Images.First()
        };
    }

    private async Task<ActorAliasResult> SaveAsync(ActorAliasRequest request, CancellationToken cancellationToken,
        Action<string> log)
    {
        try
        {
            return await _store.SaveAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            DisableStore(e, log);
            return null;
        }
    }

    private static ResolvedActor Clone(ResolvedActor actor)
    {
        return new ResolvedActor
        {
            RawName = actor.RawName,
            Name = actor.Name,
            SearchName = actor.SearchName,
            Provider = actor.Provider,
            ActorId = actor.ActorId,
            ImageSource = actor.ImageSource,
            GfriendsImageSource = actor.GfriendsImageSource
        };
    }

    private bool IsStoreDisabled => Volatile.Read(ref _storeDisabled) != 0;

    private void DisableStore(Exception error, Action<string> log)
    {
        if (Interlocked.Exchange(ref _storeDisabled, 1) == 0)
            log($"Actor alias store unavailable, fall back to actor search: {error.Message}");
    }

    private bool IsNegative(string name)
    {
        var key = NormalizeName(name);
        return key.Length > 0 && _negative.TryGetValue(key, out var until) && until > DateTime.UtcNow;
    }

    private void RememberNegative(string name)
    {
        var key = NormalizeName(name);
        if (key.Length > 0)
            _negative[key] = DateTime.UtcNow.Add(NegativeTtl);
    }

    private void ClearNegative(string name)
    {
        var key = NormalizeName(name);
        if (key.Length > 0)
            _negative.TryRemove(key, out _);
    }
}

/// <summary>把别名表接到 ApiClient；失败原样抛出，由 ActorIdentityResolver 降级。</summary>
internal sealed class ServerActorAliasStore : IActorAliasStore
{
    public async Task<IReadOnlyDictionary<string, ActorAliasResult>> LookupManyAsync(
        IReadOnlyCollection<string> names, CancellationToken cancellationToken)
    {
        var result = await ApiClient.LookupActorAliasesAsync(names, cancellationToken).ConfigureAwait(false);
        var map = new Dictionary<string, ActorAliasResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in result?.Items ?? Array.Empty<ActorAliasLookupItem>())
        {
            if (string.IsNullOrWhiteSpace(item.Name))
                continue;
            map[item.Name] = new ActorAliasResult
            {
                Found = item.Found,
                Ambiguous = item.Ambiguous,
                Person = item.Person,
                Matches = item.Matches
            };
        }
        return map;
    }

    public Task<ActorAliasResult> SaveAsync(ActorAliasRequest request, CancellationToken cancellationToken)
    {
        return ApiClient.SaveActorAliasAsync(request, cancellationToken);
    }
}

/// <summary>演员搜索与详情；详情走进程缓存，与头像阶段共享。</summary>
internal sealed class ServerActorDirectory : IActorDirectory
{
    public Task<List<ActorSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        return ApiClient.SearchActorAsync(query, cancellationToken);
    }

    public Task<ActorInfo> GetAsync(string provider, string id, CancellationToken cancellationToken)
    {
        return ActorInfoCache.GetAsync(provider, id, cancellationToken);
    }
}
