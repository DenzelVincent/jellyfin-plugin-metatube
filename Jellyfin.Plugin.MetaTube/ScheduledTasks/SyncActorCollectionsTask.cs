#if !__EMBY__
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.MetaTube.Extensions;
using Jellyfin.Plugin.MetaTube.Helpers;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
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
        foreach (var (idx, movie) in movies.WithIndex())
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report((double) idx / Math.Max(movies.Count, 1) * 15);

            // 本次扫描只用来回答「库里出现了哪些演员」。演职行不带演员 pid（服务端只回读
            // Id/Name/Role/SortOrder/Type），成员和 pid 都另按演员查，见下面按演员的循环。
            refs.AddRange(_libraryManager.GetActors(movie)
                .Select(actor => new ActorCollectionSync.ActorRef(movie.Id, actor.Name)));
        }

        var groups = ActorCollectionSync.Build(refs);
        // ReadCollections 的入参是 ProviderIds 的字典键（即 Plugin.ProviderId，值 "MetaTube"），不是 pid 里的来源段（javbus 等）：传错会静默建出没 pid 的合集。
        var index = new ActorCollectionSync.Index(_libraryManager.ReadCollections(Plugin.ProviderId));

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

                // 演员 pid 第一级：读 Person 实体，一次本地查询，不花钱。
                var pid = _libraryManager.ReadActorPid(group.DisplayName);
                var hasPid = !string.IsNullOrEmpty(pid.Provider) && !string.IsNullOrEmpty(pid.Id);
                if (hasPid)
                {
                    group.Provider = pid.Provider;
                    group.ActorId = pid.Id;
                    personPidHits++;
                }

                var existing = index.Find(group);

                // 第二级只在「本轮真的可能往合集上写 pid」时才花一次跨源搜索，判据见 ShouldSearchActorPid。
                if (!hasPid && ActorCollectionSync.ShouldSearchActorPid(group, existing, minMovieCount))
                {
                    pid = await SearchActorPidAsync(group.DisplayName, cancellationToken).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(pid.Provider) && !string.IsNullOrEmpty(pid.Id))
                    {
                        group.Provider = pid.Provider;
                        group.ActorId = pid.Id;
                        searchedPidHits++;

                        // 搜到的 pid 要写回 Person 实体，否则下一轮同一位演员还得再搜一次；
                        // 合集上那一份是跟着 plan 写的，两处都落才算根本解决。
                        await _libraryManager.WriteActorPidAsync(group.DisplayName, pid.Provider, pid.Id, _logger,
                            cancellationToken).ConfigureAwait(false);

                        // 有了 pid 要重新匹配一次：用户改过名的合集只有按 pid 才认得出来，
                        // 按同名匹配会再建一个重复合集（设计 4.2-4）。
                        existing = index.Find(group);
                    }
                }

                switch (await ApplyAsync(ActorCollectionSync.Plan(group, existing, scopedIds, minMovieCount),
                            cancellationToken).ConfigureAwait(false))
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
    /// 演员 pid 第二级：按显示名现搜。服务端要跨源查，是整轮里最贵的一步，
    /// 所以只在 ShouldSearchActorPid 说本轮用得上时才走到这里。
    /// 搜不到只当这位演员没有 pid：合集照样建，封面留给用户手动指定，不把整轮打断。
    /// </summary>
    private async Task<ProviderId> SearchActorPidAsync(string displayName, CancellationToken cancellationToken)
    {
        try
        {
            var results = await ApiClient.SearchActorAsync(displayName, cancellationToken)
                .ConfigureAwait(false);
            var first = results?.FirstOrDefault();
            return first is null
                ? ProviderId.Parse(null)
                : new ProviderId { Provider = first.Provider, Id = first.Id };
        }
        catch (OperationCanceledException)
        {
            // 用户取消或服务端下发取消时必须原样抛出，不能当成「没搜到」继续建合集。
            throw;
        }
        catch (Exception e)
        {
            _logger.Warn("Search actor pid failed: {0} ({1})", displayName, e.Message);
            return ProviderId.Parse(null);
        }
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

            // 「pid 缺失」的另一种（设计 4.5）：这位演员一条 pid 都没刮到，合集建得出来、封面解析不了。
            // 只在建立时记一次，避免每轮对齐都刷屏。
            if (string.IsNullOrEmpty(plan.ActorId))
                _logger.Warn("Created actor collection without actor pid: {0}", plan.Name);

            // 不把服务端对 ItemIdList 的处理当唯一依据：成员数对不上就补一次，本身幂等。
            // LinkedChildren 可能为 null，口径与 ReadCollections 一致。
            var children = box.LinkedChildren ?? Array.Empty<LinkedChild>();
            var missing = plan.AddItemIds.Where(id => !children.Any(c => c.ItemId == id)).ToList();
            if (missing.Count > 0)
                await _collectionManager.AddToCollectionAsync(box.Id, missing).ConfigureAwait(false);

            return plan.Action;
        }

        if (plan.CollectionId is null)
            return ActorCollectionSync.SyncAction.None;

        var collectionId = plan.CollectionId.Value;

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

        // pid 写在成员对齐之后：EnsureCollectionPidAsync 是「读整个合集 → 改 ProviderIds → 回写」，
        // 排在前面时它的快照早于本轮增删，回写有覆盖掉刚改完的成员的风险；成员失败中断时也不会留下
        // 「pid 已写但其实没对齐」的状态。
        if (plan.NeedsProviderId)
            await _libraryManager.EnsureCollectionPidAsync(collectionId, plan.Provider, plan.ActorId, _logger,
                cancellationToken).ConfigureAwait(false);

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
