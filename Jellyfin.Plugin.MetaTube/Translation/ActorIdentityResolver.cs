using System.Collections.Concurrent;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;

namespace Jellyfin.Plugin.MetaTube.Translation;

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

    /// <summary>搜索阶段拿到的首选头像（如 Gfriends），详情接口的图会先应用、它最后覆盖。</summary>
    public string ImageUrl { get; set; }

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
    Task<ActorAliasResult> LookupAsync(string name, string targetLang, CancellationToken cancellationToken);

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
        var display = string.IsNullOrWhiteSpace(parts.DisplayName) ? raw.Trim() : parts.DisplayName;
        var search = string.IsNullOrWhiteSpace(parts.SearchName) ? display : parts.SearchName;

        var lookup = await LookupAsync(raw, parts, display, search, targetLang, cancellationToken, log)
            .ConfigureAwait(false);
        if (lookup?.Found == true && !lookup.Ambiguous && lookup.Person != null)
        {
            var storedActor = new ResolvedActor
            {
                RawName = raw,
                SourceDisplayName = string.IsNullOrWhiteSpace(lookup.Person.DisplayName)
                    ? display
                    : lookup.Person.DisplayName,
                DisplayName = string.IsNullOrWhiteSpace(lookup.Person.DisplayName)
                    ? display
                    : lookup.Person.DisplayName,
                SearchName = search,
                Provider = lookup.Person.Provider,
                ActorId = lookup.Person.ActorId,
                CachedTranslatedName = lookup.TranslatedName
            };
            return storedActor;
        }

        // 表未命中（或歧义）：走演员搜索 + 详情，把名字学进表。
        var result = await SearchFirstAsync(search, display, cancellationToken, log).ConfigureAwait(false);
        if (result == null)
        {
            log($"Actor not found: {search}, keep original name");
            return new ResolvedActor
            {
                RawName = raw,
                SourceDisplayName = display,
                DisplayName = display,
                SearchName = search
            };
        }

        ActorInfo detail = null;
        try
        {
            detail = await _directory.GetAsync(result.Provider, result.Id, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            log($"Get actor info failed: {result.Provider}:{result.Id} ({e.Message})");
        }

        var canonical = detail?.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(canonical))
            display = canonical;

        var translated = translate
            ? await TranslateTextAsync(display, cancellationToken, log).ConfigureAwait(false)
            : null;

        var actor = new ResolvedActor
        {
            RawName = raw,
            SourceDisplayName = display,
            DisplayName = display,
            SearchName = search,
            Provider = detail?.Provider ?? result.Provider,
            ActorId = detail?.Id ?? result.Id,
            ImageUrl = FirstImageUrl(result),
            CachedTranslatedName = translated
        };

        if (actor.HasIdentity && !IsStoreDisabled)
        {
            var saved = await SaveAsync(BuildSaveRequest(raw, parts, actor, detail, result.Provider,
                translated, targetLang), cancellationToken, log).ConfigureAwait(false);
            if (saved?.Found == true && saved.Person != null)
            {
                actor.Provider = saved.Person.Provider;
                actor.ActorId = saved.Person.ActorId;
                if (!string.IsNullOrWhiteSpace(saved.Person.DisplayName))
                {
                    actor.SourceDisplayName = saved.Person.DisplayName;
                    actor.DisplayName = saved.Person.DisplayName;
                }
                if (!string.IsNullOrWhiteSpace(saved.TranslatedName))
                    actor.CachedTranslatedName = saved.TranslatedName;
            }
        }

        return actor;
    }

    private async Task<ActorAliasResult> LookupAsync(string raw, ActorNameHelper.Parts parts, string display,
        string search, string targetLang, CancellationToken cancellationToken, Action<string> log)
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
                var result = await _store.LookupAsync(candidate, targetLang, cancellationToken)
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

    private async Task<ActorSearchResult> SearchFirstAsync(string search, string display,
        CancellationToken cancellationToken, Action<string> log)
    {
        foreach (var query in new[] { search, display }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var results = await _directory.SearchAsync(query, cancellationToken).ConfigureAwait(false);
                if (results?.Any() == true)
                    return results.First();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception e)
            {
                log($"Search actor failed: {query} ({e.Message})");
            }
        }

        return null;
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

    private static ActorAliasRequest BuildSaveRequest(string raw, ActorNameHelper.Parts parts, ResolvedActor actor,
        ActorInfo detail, string sourceProvider, string translated, string targetLang)
    {
        var names = new List<ActorAliasNameInfo>();

        void Add(string name, string role)
        {
            if (string.IsNullOrWhiteSpace(name))
                return;
            name = name.Trim();
            if (names.Any(item => item.Name == name && item.Role == role))
                return;
            names.Add(new ActorAliasNameInfo { Name = name, Role = role });
        }

        Add(raw, ActorNameHelper.RoleAlias);
        Add(actor.SourceDisplayName, ActorNameHelper.RoleStage);
        if (!string.Equals(parts.SearchName, actor.SourceDisplayName, StringComparison.Ordinal))
            Add(parts.SearchName, ActorNameHelper.Classify(parts.SearchName));

        if (detail != null)
        {
            Add(detail.Name, ActorNameHelper.RoleStage);
            foreach (var alias in detail.Aliases ?? Array.Empty<string>())
                Add(alias, ActorNameHelper.Classify(alias));
        }

        return new ActorAliasRequest
        {
            DisplayName = actor.SourceDisplayName,
            Provider = actor.Provider,
            ActorId = actor.ActorId,
            Source = sourceProvider,
            Names = names.ToArray(),
            Translation = string.IsNullOrWhiteSpace(translated)
                ? null
                : new ActorAliasTranslationInfo { Lang = targetLang, Text = translated }
        };
    }

    private static string FirstImageUrl(ActorSearchResult result)
    {
        if (result?.Images?.Any() != true)
            return null;
        return ApiClient.GetPrimaryImageApiUrl(result.Provider, result.Id, result.Images.First(), 0.5, true);
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
            ImageUrl = actor.ImageUrl,
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
    public Task<ActorAliasResult> LookupAsync(string name, string targetLang, CancellationToken cancellationToken)
    {
        return ApiClient.LookupActorAliasAsync(name, targetLang, cancellationToken);
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
