using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.MetaTube.Configuration;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using Jellyfin.Plugin.MetaTube.Metadata;
using Jellyfin.Plugin.MetaTube.Translation;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using MovieInfo = MediaBrowser.Controller.Providers.MovieInfo;
#if __EMBY__
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;

#else
using Jellyfin.Data.Enums;
using Microsoft.Extensions.Logging;
#endif

namespace Jellyfin.Plugin.MetaTube.Providers;

#if __EMBY__
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder, IHasMetadataFeatures
#else
public class MovieProvider : BaseProvider, IRemoteMetadataProvider<Movie, MovieInfo>, IHasOrder
#endif
{
    private const string AvBase = "AVBASE";
    private const string Gfriends = "Gfriends";
    private const string Rating = "JP-18+";

    private static readonly string[] AvBaseSupportedProviderNames = { "DUGA", "FANZA", "Getchu", "MGS" };

    // 字段聚合结果缓存：GetSearchResults 阶段聚合并缓存，GetMetadata 阶段复用，
    // 避免重复全源搜索（每次刮削省掉一次 7~11s 的冗余搜索）。
    private static readonly ConcurrentDictionary<string, Metadata.MovieInfo> _aggregationCache =
        new(StringComparer.OrdinalIgnoreCase);

    // 演员身份解析：别名表 → 演员搜索/详情 → 回写；进程级缓存。
    private static readonly ActorIdentityResolver ActorResolver = new(
        new ServerActorAliasStore(), new ServerActorDirectory(), new TermActorNameTranslator());

#if __EMBY__
    public MetadataFeatures[] Features => new[]
        { MetadataFeatures.Collections, MetadataFeatures.Adult, MetadataFeatures.RequiredSetup };

    public MovieProvider(ILogManager logManager) : base(logManager.CreateLogger<MovieProvider>())
#else
    public MovieProvider(ILogger<MovieProvider> logger) : base(logger)
#endif
    {
    }

    public async Task<MetadataResult<Movie>> GetMetadata(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);
        var hasPid = !string.IsNullOrWhiteSpace(pid.Id) && !string.IsNullOrWhiteSpace(pid.Provider);

        Metadata.MovieInfo m;
        if (Configuration.EnableFieldAggregation)
        {
            // 字段级聚合：跨源按字段取优（标题/简介/封面等分源）。
            // 注意：Emby/Jellyfin 刷新（替换所有元数据）不会清除已保存的 provider id，
            // 因此即使已有 pid 也走聚合，以支持「替换所有元数据」时重新搜索。
            var keyword = SearchKeyword.Normalize(info.Name, info.Path);
            if (_aggregationCache.TryRemove(keyword, out var aggregated))
            {
                // 复用 GetSearchResults 阶段已完成的聚合结果，避免重复聚合。
                Logger.Info("Reuse aggregated movie: {0}", keyword);
                m = aggregated;
            }
            else
            {
                Logger.Info("Aggregate movie: {0}", keyword);
                m = await ApiClient.GetAggregatedMovieAsync(keyword, Configuration.GetFieldPriority(),
                    cancellationToken);
            }
        }
        else
        {
            if (!hasPid)
            {
                // Search movies and pick the first result.
                var firstResult = (await GetSearchResults(info, cancellationToken)).FirstOrDefault();
                if (firstResult != null) pid = firstResult.GetPid(Plugin.ProviderId);
            }

            Logger.Info("Get movie info: {0}", pid.ToString());
            m = await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id, cancellationToken);
        }

        // Preserve original title.
        var originalTitle = m.Title;

        // Convert to real actor names.
        if (Configuration.EnableRealActorNames)
            await ConvertToRealActorNames(m, cancellationToken);

        // 解析演员身份：拆出显示名/搜索名，查别名表，未命中走演员搜索与详情并回写。
        // 同一个 pid 的多种写法只保留一位；不同演员译成同名时由解析器回退原名。
        var resolvedActors = await ActorResolver.ResolveAllAsync(m.Actors, Configuration.TranslateActor,
            TranslationHelper.TargetLanguage, cancellationToken, message => Logger.Info("{0}", message));
        m.Actors = resolvedActors.Select(actor => actor.DisplayName).ToArray();

        // Translate movie info（标题/简介/系列/类别；演员已由解析器处理）。
        if (TranslationHelper.IsEnabled)
            await TranslateMovieInfo(m, cancellationToken);

        // Distinct and clean blank list
        m.Genres = m.Genres?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.Actors = m.Actors?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ?? Array.Empty<string>();
        m.PreviewImages = m.PreviewImages?.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct().ToArray() ??
                          Array.Empty<string>();

        // Build parameters.
        var parameters = new Dictionary<string, string>
        {
            { @"{provider}", m.Provider },
            { @"{id}", m.Id },
            { @"{number}", m.Number },
            { @"{title}", m.Title },
            { @"{series}", m.Series },
            { @"{maker}", m.Maker },
            { @"{label}", m.Label },
            { @"{director}", m.Director },
            { @"{actors}", m.Actors?.Any() == true ? string.Join(' ', m.Actors) : string.Empty },
            { @"{first_actor}", m.Actors?.FirstOrDefault() },
            { @"{year}", $"{m.ReleaseDate:yyyy}" },
            { @"{month}", $"{m.ReleaseDate:MM}" },
            { @"{date}", $"{m.ReleaseDate:yyyy-MM-dd}" }
        };

        var result = new MetadataResult<Movie>
        {
            Item = new Movie
            {
                Name = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.NameTemplate
                        : PluginConfiguration.DefaultNameTemplate, parameters),
                Tagline = RenderTemplate(
                    Configuration.EnableTemplate
                        ? Configuration.TaglineTemplate
                        : PluginConfiguration.DefaultTaglineTemplate, parameters),
                OriginalTitle = originalTitle,
                Overview = m.Summary,
                OfficialRating = Rating,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                Genres = m.Genres?.Any() == true ? m.Genres : Array.Empty<string>()
            },
            HasMetadata = true
        };

        // Set provider id.
        result.Item.SetPid(Name, m.Provider, m.Id, pid.Position);

        // Set trailer url.
        var trailerUrl = !string.IsNullOrWhiteSpace(m.PreviewVideoUrl)
            ? m.PreviewVideoUrl
            : m.PreviewVideoHlsUrl;
        if (!string.IsNullOrWhiteSpace(trailerUrl))
            result.Item.SetTrailerUrl(trailerUrl);

        // Set community rating.
        if (Configuration.EnableRatings)
            result.Item.CommunityRating = m.Score > 0 ? (float)Math.Round(m.Score * 2, 1) : null;

        // Add collection.
        if (Configuration.EnableCollections && !string.IsNullOrWhiteSpace(m.Series))
        {
            result.Item.AddCollection(m.Series);
            Logger.Info("Add Collection for movie {0} [{1}]", pid.ToString(), m.Series);
        }

        // Add studio.
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddStudio(m.Maker);

        // Add tag (series).
        if (!string.IsNullOrWhiteSpace(m.Series))
            result.Item.AddTag(m.Series);

        // Add tag (maker).
        if (!string.IsNullOrWhiteSpace(m.Maker))
            result.Item.AddTag(m.Maker);

        // Add tag (label).
        if (!string.IsNullOrWhiteSpace(m.Label))
            result.Item.AddTag(m.Label);

        // Add director.
        if (Configuration.EnableDirectors && !string.IsNullOrWhiteSpace(m.Director))
            result.AddPerson(new PersonInfo
            {
                Name = m.Director,
#if __EMBY__
                Type = PersonType.Director
#else
                Type = PersonKind.Director
#endif
            });

        // Add actors.
        foreach (var resolvedActor in resolvedActors)
        {
            var actor = new PersonInfo
            {
                Name = resolvedActor.DisplayName,
#if __EMBY__
                Type = PersonType.Actor,
#else
                Type = PersonKind.Actor,
#endif
            };
            await SetActorImageUrl(actor, resolvedActor, cancellationToken);
            result.AddPerson(actor);
        }

        return result;
    }

    public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(MovieInfo info,
        CancellationToken cancellationToken)
    {
        var pid = info.GetPid(Plugin.ProviderId);

        var searchResults = new List<MovieSearchResult>();
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
        {
            // Extract the movie number from the real file path first, otherwise
            // no provider matches (e.g. 259LUXU-1795_jasna-0.11.3_ltx_wm).
            var keyword = SearchKeyword.Normalize(info.Name, info.Path);
            if (Configuration.EnableFieldAggregation)
            {
                // 字段聚合模式下直接聚合，并缓存供 GetMetadata 复用，避免重复全源搜索。
                Logger.Info("Aggregate movie (search): {0}", keyword);
                var aggregated = await ApiClient.GetAggregatedMovieAsync(keyword,
                    Configuration.GetFieldPriority(), cancellationToken);
                _aggregationCache[keyword] = aggregated;
                searchResults.Add(aggregated);
            }
            else
            {
                Logger.Info("Search for movie: {0}", keyword);
                searchResults.AddRange(await ApiClient.SearchMovieAsync(keyword, pid.Provider,
                    cancellationToken));
            }
        }
        else
        {
            // Exact search.
            Logger.Info("Search for movie: {0}", pid.ToString());
            searchResults.Add(await ApiClient.GetMovieInfoAsync(pid.Provider, pid.Id,
                pid.Update != true, cancellationToken));
        }

        if (Configuration.EnableMovieProviderFilter)
        {
            if (Configuration.GetMovieProviderFilter() is { } filter &&
                filter.Any()) // Apply only if filter is not empty.
            {
                // Filter out mismatched results.
                searchResults.RemoveAll(m => !filter.Contains(m.Provider, StringComparer.OrdinalIgnoreCase));
                // Reorder results by stable sort.
                searchResults = searchResults.OrderBy(m =>
                    filter.FindIndex(s => s.Equals(m.Provider, StringComparison.OrdinalIgnoreCase))).ToList();
            }
            else
            {
                Logger.Warn("Movie provider filter enabled but never used");
            }
        }

        var results = new List<RemoteSearchResult>();
        if (!searchResults.Any())
        {
            Logger.Warn("Movie not found or has been filtered: {0}", pid.Id);
            return results;
        }

        foreach (var m in searchResults)
        {
            var result = new RemoteSearchResult
            {
                Name = $"[{m.Provider}] {m.Number} {m.Title}",
                SearchProviderName = Name,
                PremiereDate = m.ReleaseDate.GetValidDateTime(),
                ProductionYear = m.ReleaseDate.GetValidYear(),
                ImageUrl = ApiClient.GetPrimaryImageApiUrl(m.Provider, m.Id, m.ThumbUrl, 1.0, true)
            };
            result.SetPid(Name, m.Provider, m.Id, pid.Position);
            results.Add(result);
        }

        return results;
    }

    private async Task SetActorImageUrl(PersonInfo actor, ResolvedActor resolved,
        CancellationToken cancellationToken)
    {
        try
        {
            var provider = resolved.Provider;
            var actorId = resolved.ActorId;
            var imageUrl = resolved.ImageUrl;
            string gfriendsUrl = null;

            // 别名表没给出 pid 时才搜索：搜索同时提供 Gfriends 头像兜底。
            if (!resolved.HasIdentity)
            {
                var results = await ApiClient.SearchActorAsync(resolved.SearchName, cancellationToken);
                if (results?.Any() != true)
                {
                    Logger.Warn("Actor not found: {0}", resolved.SearchName);
                    return;
                }

                var firstResult = results.First();
                provider = firstResult.Provider;
                actorId = firstResult.Id;
                if (firstResult.Images?.Any() == true)
                {
                    imageUrl = ApiClient.GetPrimaryImageApiUrl(
                        firstResult.Provider, firstResult.Id, firstResult.Images.First(), 0.5, true);
                }

                var gfriends = results.FirstOrDefault(result =>
                    result.Provider == Gfriends && result.Images?.Any() == true);
                if (gfriends != null)
                {
                    gfriendsUrl = ApiClient.GetPrimaryImageApiUrl(
                        gfriends.Provider, gfriends.Id, gfriends.Images.First(), 0.5, true);
                }
            }

            // pid 写在有没有照片之外：服务端 SavePeopleMetadataAsync 会把 PersonInfo.ProviderIds
            // 抄进 Person 条目入库（v10.11 LibraryManager.cs:3106-3113、v12.4 :3896-3905），
            // 那是全库里唯一能持久保存演员 pid 的地方。
            actor.SetPid(Name, provider, actorId);

            // 头像优先取演员详情接口；失败时保留搜索结果里的图。
            try
            {
                var actorInfo = await ApiClient.GetActorInfoAsync(provider, actorId, cancellationToken);
                if (actorInfo?.Images?.Any() == true)
                {
                    imageUrl = ApiClient.GetPrimaryImageApiUrl(
                        actorInfo.Provider, actorInfo.Id, actorInfo.Images.First(), 0.5, true);
                }
            }
            catch (Exception e)
            {
                Logger.Warn("Get actor image from actor info failed: {0} ({1})", resolved.SearchName, e.Message);
            }

            if (!string.IsNullOrWhiteSpace(gfriendsUrl))
                imageUrl = gfriendsUrl;
            if (!string.IsNullOrWhiteSpace(imageUrl))
                actor.ImageUrl = imageUrl;
        }
        catch (Exception e)
        {
            Logger.Error("Get actor image error: {0} ({1})", resolved.SearchName, e.Message);
        }
    }

    private async Task ConvertToRealActorNames(MovieSearchResult m, CancellationToken cancellationToken)
    {
        if (!AvBaseSupportedProviderNames.Contains(m.Provider, StringComparer.OrdinalIgnoreCase)) return;

        try
        {
            var searchResults = await ApiClient.SearchMovieAsync(m.Id, AvBase, cancellationToken);
            if (searchResults?.Any() != true)
            {
                Logger.Warn("Movie not found on AVBASE: {0}", m.Id);
                return;
            }

            foreach (var result in searchResults)
            {
                var similarity = CalculateTitleSimilarity(m, result);

                Logger.Info("Calculate movie title similarity for {0} ({1}) and {2} ({3}): {4:0.00%}",
                    m.Id, m.Provider, result.Id, result.Provider, similarity);

                if (similarity >= 0.8)
                {
                    if (result.Actors?.Any() == true)
                        m.Actors = result.Actors;
                    return;
                }
            }

            Logger.Warn("No matching movie found on AVBASE for {0}", m.Id);
        }
        catch (Exception e)
        {
            Logger.Error("Convert to real actor names error: {0} ({1})", m.Number, e.Message);
        }
    }

    private static double CalculateTitleSimilarity(MovieSearchResult source, MovieSearchResult target)
    {
        var sourceKey = Normalize(source.Number + source.Title);
        var targetKey = Normalize(target.Number + target.Title);

        if (string.IsNullOrWhiteSpace(sourceKey) || string.IsNullOrWhiteSpace(targetKey))
            return 0.0;

        var distance = Levenshtein.Distance(sourceKey, targetKey);
        var avgLength = (sourceKey.Length + targetKey.Length) / 2.0;
        var similarity = 1.0 - distance / avgLength;

        return Math.Clamp(similarity, 0.0, 1.0);

        string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                return string.Empty;

            s = s.ToLowerInvariant();
            s = Regex.Replace(s, @"[\s\[\]\(\)【】（）]", "");
            return s.Trim();
        }
    }

    private async Task TranslateMovieInfo(Metadata.MovieInfo m, CancellationToken cancellationToken)
    {
        try
        {
            Logger.Info("Translate movie info: {0} => {1}", m.Number, TranslationHelper.TargetLanguage);
            await TranslationHelper.TranslateMovieAsync(m, cancellationToken,
                message => Logger.Info("{0}", message));
        }
        catch (Exception e)
        {
            Logger.Error("Translate error: {0}", e.Message);
        }
    }

    private static string RenderTemplate(string template, Dictionary<string, string> parameters)
    {
        if (string.IsNullOrWhiteSpace(template))
            return string.Empty;

        var sb = parameters.Where(kvp => template.Contains(kvp.Key))
            .Aggregate(new StringBuilder(template),
                (sb, kvp) => sb.Replace(kvp.Key, kvp.Value));

        return sb.ToString().Trim();
    }
}