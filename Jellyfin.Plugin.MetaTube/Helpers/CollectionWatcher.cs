#if !__EMBY__
using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 跟着媒体库变化即时维护合集，不必等「同步演员合集」的下一轮：
///
/// ① 影片被删除 → 去抖后扫一遍，删掉没有任何存活成员的合集；
/// ② 演员条目换图 → 把该演员的合集封面对齐过去（用户要求「演员页一改图、合集立刻跟着变」）；
/// ③ 影片元数据刷新/识别落库 → 去抖后按该批影片的演员做一次增量同步，
///    新片立刻进入对应演员合集，不必等到每日任务（2026-11-06 用户提出）。
///
/// 三件事都要去抖：删除常常成批发生，换图与整库刷新也会连续触发多次条目更新。
/// 合集/演员的写入自身会再触发 ItemUpdated，但这里影片只认元数据类型、演员只认换图，不会自循环。
/// </summary>
public sealed class CollectionWatcher : IHostedService
{
    /// <summary>删除成批发生，等这一阵安静下来再扫。</summary>
    private static readonly TimeSpan RemovalDebounce = TimeSpan.FromSeconds(5);

    /// <summary>换图会连着来几条更新，等最后一条落下再对齐。</summary>
    private static readonly TimeSpan CoverDebounce = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan CoverInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>整库刷新会连着落库上百上千部影片，攒成一批再同步，避免逐片扫一遍合集。</summary>
    private static readonly TimeSpan MovieSyncDebounce = TimeSpan.FromSeconds(5);

    /// <summary>
    /// 会改变演职表的落库类型。纯图片更新（ImageUpdate）不会换演员，不触发合集同步；
    /// 定时整理任务写的是 MetadataEdit，顺带同步一次是幂等的，不会产生额外写入。
    /// </summary>
    private const ItemUpdateType MovieSyncReasons =
        ItemUpdateType.MetadataImport | ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataEdit;

    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger<CollectionWatcher> _logger;

    /// <summary>待对齐封面的演员；重复更新只顺延时间戳，合并成一次。</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _pendingCovers = new();

    /// <summary>待增量同步的影片；重复更新只顺延时间戳，攒成一批一起处理。</summary>
    private readonly ConcurrentDictionary<Guid, DateTime> _pendingMovies = new();

    private int _cleanupRequested;
    private int _cleanupActive;
    private int _coverActive;
    private int _movieSyncActive;

    public CollectionWatcher(ILibraryManager libraryManager, ICollectionManager collectionManager,
        IProviderManager providerManager, ILogger<CollectionWatcher> logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _libraryManager.ItemRemoved += OnItemRemoved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        _libraryManager.ItemUpdated += OnItemUpdated;
        _logger.LogInformation("Collection watcher started");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _libraryManager.ItemRemoved -= OnItemRemoved;
        _libraryManager.ItemUpdated -= OnItemUpdated;
        return Task.CompletedTask;
    }

    private void OnItemRemoved(object sender, ItemChangeEventArgs e)
    {
        // 只有影片的删除会影响合集成员；删合集自身、删演员都不会产生新的空合集。
        if (e?.Item is not Movie)
            return;

        RequestCleanup();
    }

    private void RequestCleanup()
    {
        Interlocked.Exchange(ref _cleanupRequested, 1);
        if (Interlocked.CompareExchange(ref _cleanupActive, 1, 0) != 0)
            return;

        _ = Task.Run(CleanupWorkerAsync);
    }

    private async Task CleanupWorkerAsync()
    {
        try
        {
            // 整季/整库删除会连成一串事件：每收到一条就把等待窗口往后推，
            // 真正安静下来才扫，避免每删一部片就遍历一遍全部合集。
            do
            {
                await Task.Delay(RemovalDebounce).ConfigureAwait(false);
            }
            while (Interlocked.Exchange(ref _cleanupRequested, 0) == 1);

            var removed = EmptyCollectionCleaner.RemoveAll(_libraryManager, _logger);
            if (removed > 0)
                _logger.LogInformation("Removed {0} empty collection(s) after item removal", removed);
        }
        catch (Exception e)
        {
            _logger.LogWarning("Remove empty collections failed: {0}", e.Message);
        }
        finally
        {
            Interlocked.Exchange(ref _cleanupActive, 0);
            if (Volatile.Read(ref _cleanupRequested) == 1)
                RequestCleanup();
        }
    }

    private void OnItemUpdated(object sender, ItemChangeEventArgs e)
    {
        // 只跟随「演员换图」：Person 的元数据刷新（整库刷新时会成千上万条）不能都触发合集扫描。
        // 换图的落库口径是 UpdateToRepositoryAsync(ItemUpdateType.ImageUpdate)（v12.4 ImageController）。
        if (e?.Item is Person person)
        {
            if ((e.UpdateReason & ItemUpdateType.ImageUpdate) == 0)
                return;

            _pendingCovers[person.Id] = DateTime.UtcNow;
            StartCoverWorker();
            return;
        }

        // 影片元数据落库：演职表可能刚被换过，按该批影片做一次演员合集增量同步。
        if (e?.Item is not Movie movie)
            return;

        if ((e.UpdateReason & MovieSyncReasons) == 0)
            return;

        // 与计划任务同口径：只维护本插件刮出来的影片；开关关掉时整条链路不工作。
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableActorCollections)
            return;

        var pid = movie.GetPid(Plugin.ProviderId);
        if (string.IsNullOrEmpty(pid.Provider) || string.IsNullOrEmpty(pid.Id))
            return;

        _pendingMovies[movie.Id] = DateTime.UtcNow;
        StartMovieWorker();
    }

    private void StartCoverWorker()
    {
        if (Interlocked.CompareExchange(ref _coverActive, 1, 0) != 0)
            return;

        _ = Task.Run(CoverWorkerAsync);
    }

    private async Task CoverWorkerAsync()
    {
        try
        {
            while (true)
            {
                var target = Guid.Empty;
                var earliest = DateTime.MaxValue;
                foreach (var (id, at) in _pendingCovers)
                {
                    if (at < earliest)
                    {
                        earliest = at;
                        target = id;
                    }
                }

                if (target == Guid.Empty)
                    break;

                var wait = CoverDebounce - (DateTime.UtcNow - earliest);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait)
                        .ConfigureAwait(false);
                    continue;
                }

                _pendingCovers.TryRemove(target, out _);
                try
                {
                    await AlignPersonCollectionsAsync(target).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogWarning("Align collection cover after actor image change failed: {0}", e.Message);
                }

                await Task.Delay(CoverInterval).ConfigureAwait(false);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _coverActive, 0);
            if (!_pendingCovers.IsEmpty)
                StartCoverWorker();
        }
    }

    private void StartMovieWorker()
    {
        if (Interlocked.CompareExchange(ref _movieSyncActive, 1, 0) != 0)
            return;

        _ = Task.Run(MovieSyncWorkerAsync);
    }

    private async Task MovieSyncWorkerAsync()
    {
        try
        {
            while (true)
            {
                var latest = DateTime.MinValue;
                foreach (var at in _pendingMovies.Values)
                {
                    if (at > latest)
                        latest = at;
                }

                if (latest == DateTime.MinValue)
                    break;

                // 整库刷新期间事件会连续到来：只要还有新事件就把等待窗口往后推，
                // 真正安静下来才一次性处理，避免每部片各扫一遍合集。
                var wait = MovieSyncDebounce - (DateTime.UtcNow - latest);
                if (wait > TimeSpan.Zero)
                {
                    await Task.Delay(wait > TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait)
                        .ConfigureAwait(false);
                    continue;
                }

                var movieIds = _pendingMovies.Keys.ToList();
                foreach (var id in movieIds)
                    _pendingMovies.TryRemove(id, out _);

                try
                {
                    await SyncMovieCollectionsAsync(movieIds).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.LogWarning("Sync actor collections after movie update failed: {0}", e.Message);
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _movieSyncActive, 0);
            if (!_pendingMovies.IsEmpty)
                StartMovieWorker();
        }
    }

    /// <summary>
    /// 只处理这一批影片涉及的演员：成员仍按演职表取全量（与计划任务同口径），
    /// 但移除范围只限这一批影片，用户手工塞进合集的其它条目不会被误删。
    /// </summary>
    private async Task SyncMovieCollectionsAsync(IReadOnlyCollection<Guid> movieIds)
    {
        var config = Plugin.Instance?.Configuration;
        if (config is null || !config.EnableActorCollections)
            return;

        var minMovieCount = config.ActorCollectionMinMovieCount;
        if (minMovieCount < 1)
            minMovieCount = 1;

        var refs = new List<ActorCollectionSync.ActorRef>();
        var scopedIds = new HashSet<Guid>();
        foreach (var movieId in movieIds)
        {
            if (_libraryManager.GetItemById(movieId) is not Movie movie)
                continue;

            // 事件到落库之间影片可能被删或重新识别；与计划任务同口径再筛一次。
            var pid = movie.GetPid(Plugin.ProviderId);
            if (string.IsNullOrEmpty(pid.Provider) || string.IsNullOrEmpty(pid.Id))
                continue;

            scopedIds.Add(movie.Id);
            refs.AddRange(_libraryManager.GetActors(movie)
                .Select(actor => new ActorCollectionSync.ActorRef(movie.Id, actor.Name)));
        }

        if (scopedIds.Count == 0)
            return;

        var groups = ActorCollectionSync.Build(refs);
        if (groups.Count == 0)
            return;

        // ReadCollections 的入参是 ProviderIds 的字典键（即 Plugin.ProviderId），不是 pid 里的来源段。
        var index = new ActorCollectionSync.Index(_libraryManager.ReadCollections(Plugin.ProviderId));
        var updater = new ActorCollectionUpdater(_libraryManager, _collectionManager, _providerManager, _logger);

        var created = 0;
        var updated = 0;
        foreach (var group in groups.Values)
        {
            try
            {
                // 成员仍取该演员在库里的全部参演影片：新片入库时能一次补齐，
                // 也避免「只看本批影片」导致新建合集时误判阈值。
                var credited = _libraryManager.GetActorMovieIds(group.CreditNames);
                if (credited.Count > 0)
                    group.MovieIds.UnionWith(credited);

                var outcome = await updater.SyncGroupAsync(group, index, scopedIds, minMovieCount,
                    CancellationToken.None).ConfigureAwait(false);

                if (outcome.Action == ActorCollectionSync.SyncAction.Create)
                    created++;
                else if (outcome.Action == ActorCollectionSync.SyncAction.Update)
                    updated++;
            }
            catch (Exception e)
            {
                // 单个演员失败不拖垮整批，与计划任务同口径。
                _logger.LogWarning("Sync actor collection after movie update failed: {0} ({1})",
                    group.DisplayName, e.Message);
            }
        }

        if (created > 0 || updated > 0)
        {
            _logger.LogInformation(
                "Actor collections synced after movie update: {0} created, {1} updated ({2} movie(s))",
                created, updated, scopedIds.Count);
        }
    }

    private async Task AlignPersonCollectionsAsync(Guid personId)
    {
        if (_libraryManager.GetItemById(personId) is not Person person)
            return;

        var personPid = person.GetPid(Plugin.ProviderId);
        var boxes = _libraryManager.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { BaseItemKind.BoxSet }
        }).OfType<BoxSet>().ToList();

        foreach (var box in boxes)
        {
            if (!SameActor(box, person, personPid))
                continue;

            // 相册回落需要 pid：优先用演员条目上的，没有就沿用合集上的那一份。
            var boxPid = box.GetPid(Plugin.ProviderId);
            await CollectionCoverSync.AlignAsync(box, person.Name,
                string.IsNullOrEmpty(personPid.Provider) ? boxPid.Provider : personPid.Provider,
                string.IsNullOrEmpty(personPid.Id) ? boxPid.Id : personPid.Id,
                _libraryManager, _providerManager, _logger, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>合集是不是这位演员的：两边都有 pid 时按 pid 认（合集改过名也认得出），否则按名字。</summary>
    private static bool SameActor(BoxSet box, Person person, ProviderId personPid)
    {
        var boxPid = box.GetPid(Plugin.ProviderId);
        if (!string.IsNullOrEmpty(personPid.Id) && !string.IsNullOrEmpty(boxPid.Id))
        {
            return string.Equals(personPid.Provider ?? string.Empty, boxPid.Provider ?? string.Empty,
                       StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(personPid.Id, boxPid.Id, StringComparison.Ordinal);
        }

        return string.Equals((box.Name ?? string.Empty).Trim(), (person.Name ?? string.Empty).Trim(),
            StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class CollectionWatcherServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddSingleton<IHostedService, CollectionWatcher>();
    }
}
#endif
