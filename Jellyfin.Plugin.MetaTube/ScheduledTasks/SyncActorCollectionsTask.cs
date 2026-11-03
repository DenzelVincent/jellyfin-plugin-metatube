#if !__EMBY__
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.ScheduledTasks;

/// <summary>
/// 演员合集同步任务：库里唯一的合集写入者（设计 D4 路线 A）。
/// 频次交给计划任务的定时触发，默认每日 4:00，与整理元数据的 3:00 错开。
/// </summary>
public class SyncActorCollectionsTask : IScheduledTask
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly ILogger _logger;

    public SyncActorCollectionsTask(ILogger<SyncActorCollectionsTask> logger, ILibraryManager libraryManager,
        ICollectionManager collectionManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
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
        var movies = _libraryManager.GetItemList(new InternalItemsQuery
        {
            MediaTypes = new[] { MediaType.Video },
            HasAnyProviderId = new Dictionary<string, string> { { Plugin.ProviderId, string.Empty } },
            IncludeItemTypes = new[] { BaseItemKind.Movie }
        }).ToList();

        // 成员对齐的作用域：只有本次扫到的影片能被移出合集。
        var scopedIds = new HashSet<Guid>(movies.Select(m => m.Id));

        var refs = new List<ActorCollectionSync.ActorRef>();
        var pidHits = 0;
        foreach (var (idx, movie) in movies.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double) idx / Math.Max(movies.Count, 1) * 30);

            foreach (var actor in _libraryManager.GetActors(movie))
            {
                var pid = actor.GetPid(Plugin.ProviderId);
                if (!string.IsNullOrEmpty(pid.Provider) && !string.IsNullOrEmpty(pid.Id))
                    pidHits++;

                refs.Add(new ActorCollectionSync.ActorRef(movie.Id, actor.Name, pid.Provider, pid.Id));
            }
        }

        // V5 观察点：服务端回读 PersonInfo 到底带不带 MetaTube pid。全 0 说明 pid 没能跟着演员元数据落库，
        // 合集封面就解析不到。退路按设计第 3 节「演员回读路径」那一行：先用反射实测过的
        // ILibraryManager.GetPerson(name) 兜一次（Person 实体上可能带 pid），仍拿不到再退回
        // MovieProvider.SetActorImageUrl 的做法——用 ApiClient.SearchActorAsync(显示名) 现搜。
        _logger.Debug("Actor pid hits: {0} of {1} actor references", pidHits, refs.Count);

        var groups = ActorCollectionSync.Build(refs);
        // ReadCollections 的入参是 ProviderIds 的字典键（即 Plugin.ProviderId，值为 "MetaTube"），
        // 不是 pid 里的来源段（javbus 等）——传错会静默建出没 pid 的合集。
        var index = new ActorCollectionSync.Index(_libraryManager.ReadCollections(Plugin.ProviderId));

        var created = 0;
        var updated = 0;
        var skipped = 0;
        foreach (var (idx, group) in groups.Values.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(30 + (double) idx / Math.Max(groups.Count, 1) * 70);

            try
            {
                var plan = ActorCollectionSync.Plan(group, index.Find(group), scopedIds, minMovieCount);
                switch (await ApplyAsync(plan, cancellationToken).ConfigureAwait(false))
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

        _logger.Info("Actor collections synced: {0} created, {1} updated, {2} unchanged or gated",
            created, updated, skipped);

        progress?.Report(100);
    }

    private async Task<ActorCollectionSync.SyncAction> ApplyAsync(ActorCollectionSync.SyncPlan plan,
        CancellationToken cancellationToken)
    {
        // 撞舞台名要无条件记 Warn（设计 4.5）：成员本来就已对齐时 Action 是 None，走不到下面的分支。
        if (plan.NameConflict)
            _logger.Warn("Actor collection name conflict: {0} reuses a collection with a different pid ({1}, " +
                         "this scan sees {2}:{3}); rename it or clear its pid to split them",
                plan.Name, plan.CollectionId?.ToString() ?? "none", plan.Provider, plan.ActorId);

        if (plan.Action == ActorCollectionSync.SyncAction.None)
        {
            // SkipReason 非空表示被最小片数阈值挡下；为空表示本来就一致，无需写库。
            if (!string.IsNullOrEmpty(plan.SkipReason))
                _logger.Debug("Skip creating actor collection: {0}", plan.SkipReason);
            return ActorCollectionSync.SyncAction.None;
        }

        if (plan.Action == ActorCollectionSync.SyncAction.Create)
        {
            var box = await _collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = plan.Name,
                // ItemIdList 是字符串形式的 Guid（设计第 3 节反射实测）。
                ItemIdList = plan.AddItemIds.Select(id => id.ToString()).ToList(),
                ProviderIds = BuildProviderIds(plan)
            }).ConfigureAwait(false);

            _logger.Info("Create actor collection: {0} ({1} movies)", plan.Name, plan.AddItemIds.Count);

            // 不把服务端对 ItemIdList 的处理当唯一依据：成员数对不上就补一次，本身幂等。
            var missing = plan.AddItemIds.Where(id => !box.LinkedChildren.Any(c => c.ItemId == id)).ToList();
            if (missing.Count > 0)
                await _collectionManager.AddToCollectionAsync(box.Id, missing).ConfigureAwait(false);

            return plan.Action;
        }

        if (plan.CollectionId is null)
            return ActorCollectionSync.SyncAction.None;

        var collectionId = plan.CollectionId.Value;

        if (plan.NeedsProviderId)
            await _libraryManager.EnsureCollectionPidAsync(collectionId, plan.Provider, plan.ActorId, _logger,
                cancellationToken).ConfigureAwait(false);

        if (plan.AddItemIds.Count > 0)
        {
            await _collectionManager.AddToCollectionAsync(collectionId, plan.AddItemIds).ConfigureAwait(false);
            _logger.Info("Add {0} movie(s) to actor collection: {1}", plan.AddItemIds.Count, plan.Name);
        }

        if (plan.RemoveItemIds.Count > 0)
        {
            await _collectionManager.RemoveFromCollectionAsync(collectionId, plan.RemoveItemIds)
                .ConfigureAwait(false);
            _logger.Info("Remove {0} movie(s) from actor collection: {1}", plan.RemoveItemIds.Count, plan.Name);
        }

        return plan.Action;
    }

    /// <summary>
    /// 新建合集时就把演员 pid 一并写入，封面才能由 CollectionImageProvider 解析。
    /// 没有 pid 的演员照样建合集，只是封面得等用户手动指定。
    /// </summary>
    private static Dictionary<string, string> BuildProviderIds(ActorCollectionSync.SyncPlan plan)
    {
        var providerIds = new Dictionary<string, string>();
        if (string.IsNullOrEmpty(plan.Provider) || string.IsNullOrEmpty(plan.ActorId))
            return providerIds;

        providerIds[Plugin.ProviderId] = ActorCollectionSync.EncodePid(plan.Provider, plan.ActorId);
        return providerIds;
    }
}
#endif
