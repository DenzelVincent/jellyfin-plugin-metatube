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

/// <summary>一次解析得到的演员：显示名、搜索名与来源 pid。</summary>
public sealed class ResolvedActor
{
    public string RawName { get; set; }

    /// <summary>最终显示名（开启演员翻译时是译文）。</summary>
    public string DisplayName { get; set; }

    /// <summary>翻译前的显示名（汉字/艺名），撞名回退与入表用。</summary>
    public string SourceDisplayName { get; set; }

    public string SearchName { get; set; }

    public string Provider { get; set; }

    public string ActorId { get; set; }

    /// <summary>搜索阶段拿到的首选头像；详情接口的图会先应用、它最后覆盖。</summary>
    public ActorImageSource ImageSource { get; set; }

    /// <summary>Gfriends 头像兜底（搜索结果里带图时才有）。</summary>
    public ActorImageSource GfriendsImageSource { get; set; }

    internal string CachedTranslatedName { get; set; }

    public bool HasIdentity =>
        !string.IsNullOrWhiteSpace(Provider) && !string.IsNullOrWhiteSpace(ActorId);

    public string IdentityKey => HasIdentity
        ? Provider + ":" + ActorId
        : "name:" + (DisplayName ?? string.Empty).ToLowerInvariant();
}

/// <summary>服务端演员别名表的读写入口。失败时抛异常，由调用方决定是否降级。</summary>
public interface IActorAliasStore
{
    Task<ActorAliasResult> LookupAsync(string name, CancellationToken cancellationToken);

    Task<ActorAliasResult> SaveAsync(ActorAliasRequest request, CancellationToken cancellationToken);
}

/// <summary>演员搜索与详情入口。</summary>
public interface IActorDirectory
{
    Task<List<ActorSearchResult>> SearchAsync(string query, CancellationToken cancellationToken);

    Task<ActorInfo> GetAsync(string provider, string id, CancellationToken cancellationToken);
}

/// <summary>演员名翻译入口（走术语字典）。</summary>
public interface IActorNameTranslator
{
    Task<string> TranslateAsync(string name, CancellationToken cancellationToken, Action<string> log);
}

/// <summary>
/// 演员身份解析：名字拆分 → 别名表 → 演员搜索/详情 → 回写 → 翻译。
/// 别名表不可用时进程内降级为「只按演员搜索」，不打断刮削。
/// </summary>
public sealed class ActorIdentityResolver
{
    private readonly IActorAliasStore _store;
    private readonly IActorDirectory _directory;
    private readonly IActorNameTranslator _translator;
    private readonly ConcurrentDictionary<string, ResolvedActor> _cache =
        new(StringComparer.OrdinalIgnoreCase);
    private int _storeDisabled;

    public ActorIdentityResolver(IActorAliasStore store, IActorDirectory directory,
        IActorNameTranslator translator)
    {
        _store = store;
        _directory = directory;
        _translator = translator;
    }

    /// <summary>
    /// 解析一部影片的演员列表。同一个 pid 的多种写法只保留一位；
    /// 不同演员译成同名时后者回退原名。
    /// </summary>
    public async Task<List<ResolvedActor>> ResolveAllAsync(IEnumerable<string> rawNames, bool translate,
        string targetLang, CancellationToken cancellationToken, Action<string> log = null)
    {
        log ??= _ => { };
        var actors = new List<ResolvedActor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in rawNames ?? Enumerable.Empty<string>())
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;

            var actor = await ResolveOneAsync(raw, translate, targetLang, cancellationToken, log)
                .ConfigureAwait(false);
            if (actor == null || !seen.Add(actor.IdentityKey))
                continue;
            actors.Add(actor);
        }

        var sources = actors.Select(actor => actor.SourceDisplayName).ToArray();
        var displays = actors.Select(actor => actor.DisplayName).ToArray();
        var effective = ActorNameHelper.ResolveTranslatedCollisions(sources, displays);
        for (var i = 0; i < actors.Count; i++)
        {
            if (effective[i] != actors[i].DisplayName)
                log($"Actor name collision, keep original: {actors[i].SourceDisplayName} ({actors[i].DisplayName})");
            actors[i].DisplayName = effective[i];
        }

        return actors;
    }

    private async Task<ResolvedActor> ResolveOneAsync(string raw, bool translate, string targetLang,
        CancellationToken cancellationToken, Action<string> log)
    {
        var key = raw.Trim();
        if (!_cache.TryGetValue(key, out var cached))
        {
            cached = await ResolveIdentityAsync(key, translate, targetLang, cancellationToken, log)
                .ConfigureAwait(false);
            if (cached == null)
                return null;
            _cache[key] = cached;
        }

        var actor = Clone(cached);
        if (translate)
            actor.DisplayName = await TranslateAsync(actor, targetLang, cancellationToken, log)
                .ConfigureAwait(false);
        return actor;
    }

    private async Task<ResolvedActor> ResolveIdentityAsync(string raw, bool translate, string targetLang,
        CancellationToken cancellationToken, Action<string> log)
    {
        var parts = ActorNameHelper.Parse(raw);
        // B：搜索用的艺名（含假名写法）；A：数据源可能已转换好的汉字名。
        var stage = !string.IsNullOrWhiteSpace(parts.SearchName) ? parts.SearchName.Trim() : raw.Trim();
        var surface = string.IsNullOrWhiteSpace(parts.DisplayName) ? stage : parts.DisplayName.Trim();
        // A 与 B 不同时，A 直接当作艺名的译名，不再调用翻译引擎。
        var parsedTranslation = string.Equals(surface, stage, StringComparison.Ordinal) ? null : surface;

        var lookup = await LookupAsync(raw, surface, stage, cancellationToken, log)
            .ConfigureAwait(false);
        if (lookup?.Found == true && !lookup.Ambiguous && lookup.Person != null)
        {
            var translation = lookup.Person.TranslatedName;
            if (string.IsNullOrWhiteSpace(translation))
                translation = parsedTranslation;
            if (string.IsNullOrWhiteSpace(translation) && translate)
                translation = await TranslateTextAsync(stage, cancellationToken, log).ConfigureAwait(false);

            var storedStage = string.IsNullOrWhiteSpace(lookup.Person.StageName)
                ? stage
                : lookup.Person.StageName.Trim();
            var storedActor = new ResolvedActor
            {
                RawName = raw,
                SourceDisplayName = storedStage,
                DisplayName = storedStage,
                SearchName = storedStage,
                Provider = lookup.Person.Provider,
                ActorId = lookup.Person.ActorId,
                CachedTranslatedName = translation
            };

            // 命中表但还没有译名（本次翻译出了结果）时补写，下一部影片直接复用。
            if (translate && storedActor.HasIdentity && !string.IsNullOrWhiteSpace(translation) &&
                !string.Equals(translation, lookup.Person.TranslatedName, StringComparison.Ordinal))
            {
                await SaveAsync(BuildTranslationRequest(storedActor, translation),
                    cancellationToken, log).ConfigureAwait(false);
            }

            return storedActor;
        }

        // 表未命中（或歧义）：走演员搜索 + 详情，把名字学进表。
        var match = await SearchBestAsync(stage, surface, cancellationToken, log).ConfigureAwait(false);
        if (match == null)
        {
            log($"Actor not found: {stage}, keep original name");
            return new ResolvedActor
            {
                RawName = raw,
                SourceDisplayName = stage,
                DisplayName = stage,
                SearchName = stage
            };
        }

        var result = match.Result;
        var detail = match.Detail;
        var translated = parsedTranslation;
        if (string.IsNullOrWhiteSpace(translated) && translate)
            translated = await TranslateTextAsync(stage, cancellationToken, log).ConfigureAwait(false);

        var actor = new ResolvedActor
        {
            RawName = raw,
            SourceDisplayName = stage,
            DisplayName = stage,
            SearchName = stage,
            Provider = detail?.Provider ?? result.Provider,
            ActorId = detail?.Id ?? result.Id,
            ImageSource = match.Image,
            GfriendsImageSource = match.GfriendsImage,
            CachedTranslatedName = translated
        };

        if (actor.HasIdentity && !IsStoreDisabled)
        {
            var saved = await SaveAsync(BuildSaveRequest(actor, result.Provider, translated,
                parsedTranslation != null ? "source" : "engine"), cancellationToken, log).ConfigureAwait(false);
            if (saved?.Found == true && saved.Person != null)
            {
                actor.Provider = saved.Person.Provider;
                actor.ActorId = saved.Person.ActorId;
                if (string.IsNullOrWhiteSpace(actor.CachedTranslatedName) &&
                    !string.IsNullOrWhiteSpace(saved.Person.TranslatedName))
                    actor.CachedTranslatedName = saved.Person.TranslatedName;
            }
        }

        return actor;
    }

    private async Task<ActorAliasResult> LookupAsync(string raw, string display,
        string search, CancellationToken cancellationToken, Action<string> log)
    {
        if (IsStoreDisabled)
            return null;

        var candidates = new[] { search, display, raw }
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            try
            {
                var result = await _store.LookupAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                if (result?.Found == true)
                {
                    if (result.Ambiguous)
                        log($"Actor alias is ambiguous: {candidate}");
                    return result;
                }
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

        return null;
    }

    // 身份来源按演员数据质量排序：MINNANO 有汉字/假名/拼音/别名，AVBASE 有真实姓名，
    // Gfriends 只有图片（详情里的图服务端会另行注入），所以只作最后兜底。
    private const string Gfriends = "Gfriends";

    private static readonly string[] ActorProviderPreference =
    {
        "MINNANO", "AVBASE", "AV-LEAGUE", "ThePornDB", Gfriends, "Wikipedia"
    };

    private const int MaxDetailCandidates = 3;

    /// <summary>
    /// 在多个来源的搜索结果里挑身份：先按数据质量排序，优先汉字名；再逐个取详情，
    /// 用 name/aliases 与查询名做匹配校验。都校验不上时退回排序第一的候选并记日志。
    /// </summary>
    private sealed class ActorSearchMatch
    {
        public ActorSearchResult Result { get; set; }

        public ActorInfo Detail { get; set; }

        public ActorImageSource Image { get; set; }

        public ActorImageSource GfriendsImage { get; set; }
    }

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
            ActorInfo fallbackDetail = null;
            foreach (var candidate in ranked.Take(MaxDetailCandidates))
            {
                ActorInfo detail;
                try
                {
                    detail = await _directory.GetAsync(candidate.Provider, candidate.Id, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    log($"Get actor info failed: {candidate.Provider}:{candidate.Id} ({e.Message})");
                    continue;
                }

                if (detail == null)
                    continue;

                fallbackResult ??= candidate;
                fallbackDetail ??= detail;

                if (queries.Any(name => MatchesDetail(detail, name)))
                {
                    return new ActorSearchMatch
                    {
                        Result = candidate,
                        Detail = detail,
                        Image = ToImageSource(candidate),
                        GfriendsImage = gfriendsImage
                    };
                }
            }

            if (fallbackResult != null)
            {
                log($"No verified actor match: {query}, use {fallbackResult.Provider}:{fallbackResult.Id}");
                return new ActorSearchMatch
                {
                    Result = fallbackResult,
                    Detail = fallbackDetail,
                    Image = ToImageSource(fallbackResult),
                    GfriendsImage = gfriendsImage
                };
            }
        }

        return null;
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

    private static bool MatchesDetail(ActorInfo detail, string query)
    {
        var key = NormalizeName(query);
        if (key.Length == 0)
            return false;
        if (NormalizeName(detail.Name) == key)
            return true;
        foreach (var alias in detail.Aliases ?? Array.Empty<string>())
        {
            if (NormalizeName(alias) == key)
                return true;
        }
        return false;
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

    private async Task<string> TranslateAsync(ResolvedActor actor, string targetLang,
        CancellationToken cancellationToken, Action<string> log)
    {
        if (!string.IsNullOrWhiteSpace(actor.CachedTranslatedName))
            return actor.CachedTranslatedName;

        var translated = await TranslateTextAsync(actor.SourceDisplayName, cancellationToken, log)
            .ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(translated))
            actor.CachedTranslatedName = translated;
        return string.IsNullOrWhiteSpace(translated) ? actor.SourceDisplayName : translated;
    }

    private async Task<string> TranslateTextAsync(string text, CancellationToken cancellationToken, Action<string> log)
    {
        if (string.IsNullOrWhiteSpace(text))
            return text;
        try
        {
            return await _translator.TranslateAsync(text, cancellationToken, log).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log($"Translate actor name failed: {text} ({e.Message})");
            return text;
        }
    }

    private static ActorAliasRequest BuildSaveRequest(ResolvedActor actor, string sourceProvider,
        string translated, string translationSource)
    {
        return new ActorAliasRequest
        {
            StageName = actor.SourceDisplayName,
            TranslatedName = translated,
            TranslationSource = translationSource,
            Provider = actor.Provider,
            ActorId = actor.ActorId,
            Source = sourceProvider
        };
    }

    private static ActorAliasRequest BuildTranslationRequest(ResolvedActor actor, string translated)
    {
        return new ActorAliasRequest
        {
            StageName = actor.SourceDisplayName,
            TranslatedName = translated,
            TranslationSource = "engine",
            Provider = actor.Provider,
            ActorId = actor.ActorId,
            Source = "lookup"
        };
    }

    private static ResolvedActor Clone(ResolvedActor actor)
    {
        return new ResolvedActor
        {
            RawName = actor.RawName,
            DisplayName = actor.DisplayName,
            SourceDisplayName = actor.SourceDisplayName,
            SearchName = actor.SearchName,
            Provider = actor.Provider,
            ActorId = actor.ActorId,
            ImageSource = actor.ImageSource,
            GfriendsImageSource = actor.GfriendsImageSource,
            CachedTranslatedName = actor.CachedTranslatedName
        };
    }

    private bool IsStoreDisabled => Volatile.Read(ref _storeDisabled) != 0;

    private void DisableStore(Exception error, Action<string> log)
    {
        if (Interlocked.Exchange(ref _storeDisabled, 1) == 0)
            log($"Actor alias store unavailable, fall back to actor search: {error.Message}");
    }
}

/// <summary>把别名表接到 ApiClient；失败原样抛出，由 ActorIdentityResolver 降级。</summary>
internal sealed class ServerActorAliasStore : IActorAliasStore
{
    public Task<ActorAliasResult> LookupAsync(string name, CancellationToken cancellationToken)
    {
        return ApiClient.LookupActorAliasAsync(name, cancellationToken);
    }

    public Task<ActorAliasResult> SaveAsync(ActorAliasRequest request, CancellationToken cancellationToken)
    {
        return ApiClient.SaveActorAliasAsync(request, cancellationToken);
    }
}

/// <summary>演员搜索与详情。</summary>
internal sealed class ServerActorDirectory : IActorDirectory
{
    public Task<List<ActorSearchResult>> SearchAsync(string query, CancellationToken cancellationToken)
    {
        return ApiClient.SearchActorAsync(query, cancellationToken);
    }

    public Task<ActorInfo> GetAsync(string provider, string id, CancellationToken cancellationToken)
    {
        return ApiClient.GetActorInfoAsync(provider, id, cancellationToken);
    }
}

/// <summary>演员名翻译走服务端术语字典（与系列/类别同一套缓存）。</summary>
internal sealed class TermActorNameTranslator : IActorNameTranslator
{
    public Task<string> TranslateAsync(string name, CancellationToken cancellationToken, Action<string> log)
    {
        return TranslationHelper.TranslateActorNameAsync(name, cancellationToken, log);
    }
}
