#if !__EMBY__
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Collections;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 维护「无演员影片」合集：把带 MetaTube id、但演职表里没有任何演员的影片集中到一个 BoxSet。
/// 全库任务与单片增量同步共用；合集没有 MetaTube pid，不会参与演员合集的同名/pid 匹配，
/// 也不会被演员封面同步当成演员合集处理。
/// </summary>
public static class NoActorCollectionSync
{
    public const string CollectionName = "无演员影片";

    /// <summary>
    /// 对齐合集：noActorMovieIds 要收进去，withActorMovieIds 是移除作用域。
    /// 合集不存在时只在确实有目标影片时才创建；没有目标且合集也不存在时什么都不做。
    /// </summary>
    public static async Task SyncAsync(ILibraryManager libraryManager, ICollectionManager collectionManager,
        IReadOnlyCollection<Guid> noActorMovieIds, IReadOnlyCollection<Guid> withActorMovieIds,
        IReadOnlyList<ActorCollectionSync.ExistingCollection> collections, ILogger logger,
        CancellationToken cancellationToken)
    {
        var existing = collections.FirstOrDefault(collection =>
            string.Equals((collection.Name ?? string.Empty).Trim(), CollectionName,
                StringComparison.OrdinalIgnoreCase));

        var plan = ActorCollectionSync.PlanNoActorCollection(noActorMovieIds, withActorMovieIds, existing);

        if (plan.Create)
        {
            var box = await collectionManager.CreateCollectionAsync(new CollectionCreationOptions
            {
                Name = CollectionName,
                ItemIdList = plan.AddItemIds.Select(id => id.ToString()).ToList()
            }).ConfigureAwait(false);

            logger.Info("Create no-actor collection: {0} ({1} movies)", CollectionName, plan.AddItemIds.Count);

            // 不把服务端对 ItemIdList 的处理当唯一依据：成员数对不上就补一次，本身幂等。
            var children = box.LinkedChildren ?? Array.Empty<LinkedChild>();
            var missing = plan.AddItemIds.Where(id => !children.Any(child => child.ItemId == id)).ToList();
            if (missing.Count > 0)
                await collectionManager.AddToCollectionAsync(box.Id, missing).ConfigureAwait(false);

            await libraryManager.SyncCollectionCountRatingAsync(box.Id, logger, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        if (existing is null)
            return;

        if (plan.AddItemIds.Count > 0)
        {
            await collectionManager.AddToCollectionAsync(existing.Id, plan.AddItemIds).ConfigureAwait(false);
            logger.Info("Add {0} movie(s) to no-actor collection", plan.AddItemIds.Count);
        }

        if (plan.RemoveItemIds.Count > 0)
        {
            await collectionManager.RemoveFromCollectionAsync(existing.Id, plan.RemoveItemIds)
                .ConfigureAwait(false);
            logger.Info("Remove {0} movie(s) from no-actor collection", plan.RemoveItemIds.Count);
        }

        // 单片刷新把最后一个成员移出后也立即删掉合集，不等每日任务里的 EmptyCollectionCleaner。
        // 本轮有新增时不判空：AddToCollectionAsync 刚写完，缓存里的 LinkedChildren 可能还没刷新。
        if (plan.AddItemIds.Count == 0)
            DeleteIfEmpty(libraryManager, existing.Id, logger);

        // 上一步可能刚把合集删掉，这里取不到条目就由 helper 自己返回，不会写库。
        await libraryManager.SyncCollectionCountRatingAsync(existing.Id, logger, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void DeleteIfEmpty(ILibraryManager libraryManager, Guid collectionId, ILogger logger)
    {
        if (libraryManager.GetItemById(collectionId) is not BoxSet box)
            return;

        if ((box.LinkedChildren ?? Array.Empty<LinkedChild>()).Any(child => child.ItemId.HasValue))
            return;

        // 与 Jellyfin 自己删除合集一致：条目与合集目录一起删，否则空目录会被下次扫描捡回来。
        libraryManager.DeleteItem(box, new DeleteOptions { DeleteFileLocation = true });
        logger.Info("Remove empty no-actor collection: {0}", box.Name);
    }
}
#endif
