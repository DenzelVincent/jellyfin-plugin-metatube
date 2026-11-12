#if !__EMBY__
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

/// <summary>
/// 演员合集的全库同步任务：每日兜底（设计 D4 路线 A 的定时入口）。
/// 单片元数据刷新后的增量同步由 CollectionWatcher 走同一个 ActorCollectionUpdater；
/// 本任务默认每日 4:00，与整理元数据的 3:00 错开。
/// </summary>
public class SyncActorCollectionsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger _logger;

    public SyncActorCollectionsTask(ILogger<SyncActorCollectionsTask> logger, ILibraryManager libraryManager,
        ICollectionManager collectionManager, IProviderManager providerManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
    }

    public string Key => $"{Plugin.ProviderName}SyncActorCollections";

    public string Name => "同步演员合集";

    public string Description =>
        $"按 {Plugin.ProviderName} 元数据中的演员全量重建演员合集：先删除插件维护的旧合集，再按当前数据显示名重新创建。";

    public string Category => Plugin.ProviderName;

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.DailyTrigger,
            TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
        };
    }

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        await Task.Yield();

        var config = Plugin.Instance.Configuration;
        if (!config.EnableActorCollections)
        {
            _logger.Info("Actor collections disabled, nothing to do");
            progress?.Report(100);
            return;
        }

        var minMovieCount = config.ActorCollectionMinMovieCount;
        if (minMovieCount < 1)
        {
            _logger.Warn("Invalid actor collection minimum movie count: {0}, fallback to 1", minMovieCount);
            minMovieCount = 1;
        }

        // 空范围 = 全部库，旧行为不变；非空时它既是扫描范围，也是按演员反查成员的范围。
        var libraryScope = ActorCollectionScope.ParseLibraryIds(config.ActorCollectionLibraryIds);

        progress?.Report(0);

        // 查询条件与 OrganizeMetadataTask 一致：只处理本插件刮出来的影片。
        // 库里历史数据的 provider key 有 "MetaTube"/"metatube" 两种写法，数据库查询大小写敏感，
        // 所以过滤条件必须把两种写法都带上；不能只查一种，也不能靠内存过滤（GetItemList 可能只返回子集）。
        var movieQuery = new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            IncludeItemTypes = new[] { BaseItemKind.Movie },
            HasAnyProviderId = JellyfinExtensions.BuildMetaTubeProviderFilter()
        };

        List<BaseItem> movies;
        HashSet<Guid> allMetaTubeIds;
        if (libraryScope.Length == 0)
        {
            // 空范围不额外发一次只取 id 的查询，保持旧路径。
            movies = _libraryManager.GetItemList(movieQuery).ToList();
            allMetaTubeIds = movies.Select(m => m.Id).ToHashSet();
        }
        else
        {
            // 范围外的影片不参与分组，但必须留在移除作用域里：
            // 把某个库从白名单摘掉后，它的旧成员要靠这次扫描才删得掉。
            allMetaTubeIds = _libraryManager.GetItemIds(movieQuery).ToHashSet();
            movieQuery.AncestorIds = libraryScope;
            movies = _libraryManager.GetItemList(movieQuery).ToList();
        }

        // 成员对齐的作用域：只有本次扫到的 MetaTube 影片能被移出合集。
        var scopedIds = allMetaTubeIds;
        var inScopeIds = movies.Select(m => m.Id).ToHashSet();
        if (libraryScope.Length > 0)
            _logger.Info("Actor collection library scope: {0} library(ies), {1} movie(s) in scope, {2} out of scope",
                libraryScope.Length, inScopeIds.Count, allMetaTubeIds.Count - inScopeIds.Count);

        var refs = new List<ActorCollectionSync.ActorRef>();
        var noActorIds = new List<Guid>();
        var withActorIds = new List<Guid>();
        var multiActorIds = new List<Guid>();
        foreach (var (idx, movie) in movies.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double) idx / Math.Max(movies.Count, 1) * 15);

            // 本次扫描只用来回答「库里出现了哪些演员」。演职行不带演员 pid（服务端只回读
            // Id/Name/Role/SortOrder/Type），成员和 pid 都另按演员查，见下面按演员的循环。
            var actors = _libraryManager.GetActors(movie);
            var actorNames = actors.Select(actor => actor.Name)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToList();
            if (actorNames.Count == 0)
            {
                noActorIds.Add(movie.Id);
                continue;
            }

            withActorIds.Add(movie.Id);
            // 多人共演只进专门合集，不再进入普通演员合集；移除范围仍包含它，旧成员会被清出。
            if (config.EnableMultiActorCollection && ActorCollectionSync.IsMultiActor(actorNames))
            {
                multiActorIds.Add(movie.Id);
                continue;
            }

            refs.AddRange(actors.Select(actor => new ActorCollectionSync.ActorRef(movie.Id, actor.Name)));
        }

        // 无演员合集的移除作用域：本批有演员的影片 + 白名单外的影片。
        // 后者即使本轮不参与分组，也要能从旧合集里清掉；手工加入的非 MetaTube 条目仍不在作用域内。
        var noActorRemoveScope = new HashSet<Guid>(withActorIds);
        if (libraryScope.Length > 0)
            noActorRemoveScope.UnionWith(allMetaTubeIds.Where(id => !inScopeIds.Contains(id)));

        var groups = ActorCollectionSync.Build(refs);

        // 与单片增量互斥：整个删除+重建期间持有同一把锁，增量请求排队等待。
        using var collectionLock = await ActorCollectionLock.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);

        // 全量重建：先删除插件维护的旧合集（带 MetaTube pid 的演员合集、多人共演、无演员影片），
        // 再以空的现有合集列表创建新合集。合集 ID、手工改名与手工成员都会重置（用户口径）。
        var existingCollections = _libraryManager.ReadCollections(Plugin.ProviderId);
        var removedCollections = RemoveCollectionsForRebuild(existingCollections, cancellationToken);
        if (removedCollections > 0)
            _logger.Info("Full actor collection rebuild: removed {0} collection(s)", removedCollections);

        var collections = new List<ActorCollectionSync.ExistingCollection>();
        var index = new ActorCollectionSync.Index(collections);
        var updater = new ActorCollectionUpdater(_libraryManager, _collectionManager, _providerManager, _logger);

        var created = 0;
        var updated = 0;
        var skipped = 0;
        var personPidHits = 0;
        var searchedPidHits = 0;
        foreach (var (idx, group) in groups.Values.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(15 + (double) idx / Math.Max(groups.Count, 1) * 80);

            try
            {
                // 成员按演职表取全量：本次扫描只覆盖带 MetaTube pid 的影片，按它算成员会漏片
                // （演员页能看到 4 部、合集里只有 1 部即由此而来）。一条都没查到时保持本次扫描的结果，
                // 那是演职名写法与库里登记不一致时唯一的依据；移除范围仍限在 scopedIds 内，不会清空合集。
                var credited = _libraryManager.GetActorMovieIds(group.CreditNames, libraryScope);
                if (credited.Count > 0)
                    group.MovieIds.UnionWith(credited);

                var outcome = await updater.SyncGroupAsync(group, index, scopedIds, minMovieCount,
                    cancellationToken).ConfigureAwait(false);
                if (outcome.PidSource == ActorCollectionUpdater.PidSource.PersonItem)
                    personPidHits++;
                else if (outcome.PidSource == ActorCollectionUpdater.PidSource.OnlineSearch)
                    searchedPidHits++;

                switch (outcome.Action)
                {
                    case ActorCollectionSync.SyncAction.Create:
                        created++;
                        break;
                    case ActorCollectionSync.SyncAction.Update:
                        updated++;
                        break;
                    default:
                        skipped++;
                        break;
                }
            }
            catch (Exception e)
            {
                // 单个演员失败只记日志并继续，与 OrganizeMetadataTask 同口径（设计 4.3）。
                _logger.Error("Sync actor collection error: {0} ({1})", group.DisplayName, e.Message);
            }
        }

        // 无演员影片单独归集；withActorIds 只用于移出，不碰手工加入的非 MetaTube 条目。
        try
        {
            await NoActorCollectionSync.SyncAsync(_libraryManager, _collectionManager, noActorIds, noActorRemoveScope,
                collections, _logger, cancellationToken).ConfigureAwait(false);
            _logger.Debug("No-actor movies: {0}, removal scope: {1} (with actors: {2})", noActorIds.Count,
                noActorRemoveScope.Count, withActorIds.Count);
        }
        catch (Exception e)
        {
            _logger.Error("Sync no-actor collection error: {0}", e.Message);
        }

        // 多人共演合集：只收本次扫描范围内、演员数超过阈值的 MetaTube 影片；
        // 移除范围是本次扫到的全部 MetaTube 影片，降到 2 人及以下的会被移出。
        if (config.EnableMultiActorCollection)
        {
            try
            {
                await MultiActorCollectionSync.SyncAsync(_libraryManager, _collectionManager, multiActorIds, scopedIds,
                    collections, _logger, cancellationToken).ConfigureAwait(false);
                _logger.Debug("Multi-actor movies: {0}", multiActorIds.Count);
            }
            catch (Exception e)
            {
                _logger.Error("Sync multi-actor collection error: {0}", e.Message);
            }
        }

        // 空合集清理：即时清理交给 CollectionWatcher 的事件监听，这里兜住「停机期间删了影片」的情况。
        // 口径一致：不区分是不是演员合集，没有任何存活成员的合集一律删。
        var removed = EmptyCollectionCleaner.RemoveAll(_libraryManager, _logger);
        if (removed > 0)
            _logger.Info("Removed {0} empty collection(s)", removed);

        // 设计 4.5 要「pid 缺失记 Warn」，但库里演员成千上万，逐个 Warn 会把日志冲干净，所以整轮汇总成两条。
        // personPidHits 为 0 而 searchedPidHits 很高 = 刮削期写的 pid 没跟着落库
        // （库里这批影片不是本插件刮的，或 Person 条目被 People 校验的清理环节删过），
        // 表现是这一轮几乎每位演员都要走一次在线搜索，任务明显变慢。
        _logger.Debug("Actor pids resolved: {0} from person items, {1} by online search, of {2} groups",
            personPidHits, searchedPidHits, groups.Count);
        if (groups.Count > 0 && personPidHits == 0)
            _logger.Warn("No MetaTube pid found on any Person item in this library; actor collection covers " +
                         "depend on online searches this run. Refresh the movies with {0} once to persist " +
                         "actor pids", Plugin.ProviderName);

        _logger.Info("Actor collections synced: {0} created, {1} updated, {2} unchanged or gated",
            created, updated, skipped);

        progress?.Report(100);
    }

    /// <summary>
    /// 删除插件维护的旧合集：带 MetaTube pid 的演员合集，以及固定名字的
    /// 「多人共演」「无演员影片」。用户自定义名、无 pid 的普通合集不动。
    /// 单个删除失败只记 Warn，继续重建其余合集。
    /// </summary>
    private int RemoveCollectionsForRebuild(
        IReadOnlyCollection<ActorCollectionSync.ExistingCollection> collections,
        CancellationToken cancellationToken)
    {
        var removed = 0;
        foreach (var collection in collections)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var isPluginActor = !string.IsNullOrEmpty(collection.ActorId);
            var isSpecial = IsRebuildSpecialName(collection.Name);
            if (!isPluginActor && !isSpecial)
                continue;

            if (_libraryManager.GetItemById(collection.Id) is not BoxSet box)
                continue;

            try
            {
                // 与 Jellyfin 自己删除合集一致：条目与合集目录一起删。
                _libraryManager.DeleteItem(box, new DeleteOptions { DeleteFileLocation = true });
                removed++;
            }
            catch (Exception e)
            {
                _logger.Warn("Remove collection for full rebuild failed: {0} ({1})", collection.Name, e.Message);
            }
        }

        return removed;
    }

    private static bool IsRebuildSpecialName(string name)
    {
        var value = (name ?? string.Empty).Trim();
        return string.Equals(value, MultiActorCollectionSync.CollectionName, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(value, NoActorCollectionSync.CollectionName, StringComparison.OrdinalIgnoreCase);
    }
}
#endif
