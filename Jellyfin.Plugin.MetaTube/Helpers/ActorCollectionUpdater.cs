#if !__EMBY__
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员合集逐组写入：解析演员 pid、算差集、建/补合集、对齐封面。
/// 计划任务（全库）与 CollectionWatcher（单片刷新后的增量）共用这一份，
/// 保证两条入口的阈值、pid 回写、同名冲突与「手工条目不动」口径完全一致。
/// </summary>
public sealed class ActorCollectionUpdater
{
    private readonly ILibraryManager _libraryManager;
    private readonly ICollectionManager _collectionManager;
    private readonly IProviderManager _providerManager;
    private readonly ILogger _logger;

    public ActorCollectionUpdater(ILibraryManager libraryManager, ICollectionManager collectionManager,
        IProviderManager providerManager, ILogger logger)
    {
        _libraryManager = libraryManager;
        _collectionManager = collectionManager;
        _providerManager = providerManager;
        _logger = logger;
    }

    /// <summary>演员 pid 的来源，供计划任务汇总 Debug 日志用（增量入口不统计）。</summary>
    public enum PidSource
    {
        None,
        PersonItem,
        OnlineSearch
    }

    public readonly record struct SyncOutcome(ActorCollectionSync.SyncAction Action, PidSource PidSource);

    /// <summary>
    /// 处理一位演员：先补 pid，再按现有合集算差集并写入。
    /// 单组失败由调用方 catch，与计划任务「单个演员失败只记日志并继续」同口径。
    /// </summary>
    public async Task<SyncOutcome> SyncGroupAsync(ActorCollectionSync.ActorGroup group,
        ActorCollectionSync.Index index, ICollection<Guid> scopedMovieIds, int minMovieCount,
        CancellationToken cancellationToken)
    {
        var pidSource = PidSource.None;

        // 演员 pid 第一级：读 Person 实体，一次本地查询，不花钱。
        var pid = _libraryManager.ReadActorPid(group.DisplayName);
        var hasPid = !string.IsNullOrEmpty(pid.Provider) && !string.IsNullOrEmpty(pid.Id);
        if (hasPid)
        {
            group.Provider = pid.Provider;
            group.ActorId = pid.Id;
            pidSource = PidSource.PersonItem;
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
                pidSource = PidSource.OnlineSearch;

                // 搜到的 pid 要写回 Person 实体，否则下一轮同一位演员还得再搜一次；
                // 合集上那一份是跟着 plan 写的，两处都落才算根本解决。
                await _libraryManager.WriteActorPidAsync(group.DisplayName, pid.Provider, pid.Id, _logger,
                    cancellationToken).ConfigureAwait(false);

                // 有了 pid 要重新匹配一次：用户改过名的合集只有按 pid 才认得出来，
                // 按同名匹配会再建一个重复合集（设计 4.2-4）。
                existing = index.Find(group);
            }
        }

        var action = await ApplyAsync(ActorCollectionSync.Plan(group, existing, scopedMovieIds, minMovieCount),
            cancellationToken).ConfigureAwait(false);

        return new SyncOutcome(action, pidSource);
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

            // 成员这轮没变，但封面仍要对齐：演员换了 Person 头像也要跟着换（用户口径）。
            await AlignCoverAsync(plan, cancellationToken).ConfigureAwait(false);
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

            // 新建的合集立刻对齐一次封面，首次打开就有演员图，不必等下一轮或一次图片刷新。
            await AlignCoverAsync(box, plan, cancellationToken).ConfigureAwait(false);

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

        // 成员与 pid 都写完之后再对齐封面：封面用的是演员 Person 条目，不依赖合集上的 pid，
        // 但放在后面可以让「新建/改成员」的那一次刷新与换图错开。
        await AlignCoverAsync(plan, cancellationToken).ConfigureAwait(false);

        return plan.Action;
    }

    /// <summary>按 plan 找回已有合集并对齐封面；没有合集（如被阈值挡下）时什么都不做。</summary>
    private async Task AlignCoverAsync(ActorCollectionSync.SyncPlan plan, CancellationToken cancellationToken)
    {
        if (plan.CollectionId is not { } collectionId)
            return;

        if (_libraryManager.GetItemById(collectionId) is not BoxSet collection)
            return;

        await AlignCoverAsync(collection, plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task AlignCoverAsync(BoxSet collection, ActorCollectionSync.SyncPlan plan,
        CancellationToken cancellationToken)
    {
        await CollectionCoverSync.AlignAsync(collection, plan.Name, plan.Provider, plan.ActorId, _libraryManager,
            _providerManager, _logger, cancellationToken).ConfigureAwait(false);
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
