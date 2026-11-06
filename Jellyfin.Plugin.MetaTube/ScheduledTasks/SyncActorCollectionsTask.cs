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

    public string Description => $"按 {Plugin.ProviderName} 元数据中的演员自动创建并维护演员合集。";

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

        progress?.Report(0);

        // 查询条件与 OrganizeMetadataTask 一致：只处理本插件刮出来的影片。
        // 不能加 HasAnyProviderId：库里历史数据的 provider key 有 "MetaTube"/"metatube" 两种写法，
        // 数据库查询大小写敏感会漏片；改为查全部影片后按内存里的 ProviderIds（OrdinalIgnoreCase）过滤。
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
        }).Where(movie => movie.HasMetaTubePid()).ToList();

        // 成员对齐的作用域：只有本次扫到的影片能被移出合集。
        var scopedIds = new HashSet<Guid>(movies.Select(m => m.Id));

        var refs = new List<ActorCollectionSync.ActorRef>();
        var noActorIds = new List<Guid>();
        var withActorIds = new List<Guid>();
        foreach (var (idx, movie) in movies.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double) idx / Math.Max(movies.Count, 1) * 15);

            // 本次扫描只用来回答「库里出现了哪些演员」。演职行不带演员 pid（服务端只回读
            // Id/Name/Role/SortOrder/Type），成员和 pid 都另按演员查，见下面按演员的循环。
            var actors = _libraryManager.GetActors(movie);
            if (actors.Any(actor => !string.IsNullOrWhiteSpace(actor.Name)))
                withActorIds.Add(movie.Id);
            else
                noActorIds.Add(movie.Id);

            refs.AddRange(actors.Select(actor => new ActorCollectionSync.ActorRef(movie.Id, actor.Name)));
        }

        var groups = ActorCollectionSync.Build(refs);
        // ReadCollections 的入参是 ProviderIds 的字典键（即 Plugin.ProviderId，值 "MetaTube"），不是 pid 里的来源段（javbus 等）：传错会静默建出没 pid 的合集。
        var collections = _libraryManager.ReadCollections(Plugin.ProviderId);
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
                var credited = _libraryManager.GetActorMovieIds(group.CreditNames);
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
            await NoActorCollectionSync.SyncAsync(_libraryManager, _collectionManager, noActorIds, withActorIds,
                collections, _logger, cancellationToken).ConfigureAwait(false);
            _logger.Debug("No-actor movies: {0}, with actors: {1}", noActorIds.Count, withActorIds.Count);
        }
        catch (Exception e)
        {
            _logger.Error("Sync no-actor collection error: {0}", e.Message);
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

}
#endif
