#if !__EMBY__
#pragma warning disable CA2254

using Jellyfin.Data.Enums;                  // BaseItemKind / PersonKind
using Jellyfin.Plugin.MetaTube.Helpers;     // ActorCollectionSync
using MediaBrowser.Controller.Entities;     // BaseItem / InternalItemsQuery
using MediaBrowser.Controller.Entities.Movies;  // BoxSet（原有）
using MediaBrowser.Controller.Library;      // ILibraryManager / ItemUpdateType
using MediaBrowser.Controller.Providers;    // PersonInfo
using Microsoft.Extensions.Logging;         // ILogger（原有）

namespace Jellyfin.Plugin.MetaTube.Extensions;

public static class JellyfinExtensions
{
    #region Logger

    public static void Debug(this ILogger logger, string message, params object[] args)
    {
        logger.LogDebug(message, args);
    }

    public static void Info(this ILogger logger, string message, params object[] args)
    {
        logger.LogInformation(message, args);
    }

    public static void Warn(this ILogger logger, string message, params object[] args)
    {
        logger.LogWarning(message, args);
    }

    public static void Error(this ILogger logger, string message, params object[] args)
    {
        logger.LogError(message, args);
    }

    #endregion

    #region Movie

    public static void AddCollection(this Movie movie, string name)
    {
        movie.CollectionName = name;
    }

    #endregion

    #region Collection

    /// <summary>
    /// 取影片的演员清单。10.11 的 BaseItem 上没有 People 属性，只能走 ILibraryManager.GetPeople；
    /// PersonInfo 到底带不带 MetaTube pid 属服务端行为（设计 V5），由调用方统计命中率。
    /// </summary>
    public static IReadOnlyList<PersonInfo> GetActors(this ILibraryManager manager, BaseItem item)
    {
        return manager.GetPeople(item).Where(p => p.Type == PersonKind.Actor).ToList();
    }

    /// <summary>
    /// 一次查询拿全部 BoxSet，折算成 ActorCollectionSync.Index 用的轻量记录。
    /// 不按 pid 过滤：用户手建的同名合集没有 pid，也要被查到才能复用（设计 4.2-4）。
    /// 成员只读 LinkedChildren 的 ItemId，不逐个解析条目。
    /// </summary>
    public static List<ActorCollectionSync.ExistingCollection> ReadCollections(this ILibraryManager manager,
        string providerName)
    {
        var result = new List<ActorCollectionSync.ExistingCollection>();
        foreach (var box in manager.GetItemList(new InternalItemsQuery
                 {
                     IncludeItemTypes = new[] { BaseItemKind.BoxSet }
                 }).OfType<BoxSet>())
        {
            var pid = box.GetPid(providerName);
            result.Add(new ActorCollectionSync.ExistingCollection
            {
                Id = box.Id,
                Name = box.Name,
                Provider = pid.Provider,
                ActorId = pid.Id,
                // 本仓库没开可空注解，LinkedChildren 是普通数组属性，老数据/别的插件建的合集可能带 null 进来，
                // 这里按空集处理即「成员为空」，与「合集确实没成员」同口径，不能让整个扫描抛 NRE。
                MemberIds = new HashSet<Guid>((box.LinkedChildren ?? Array.Empty<LinkedChild>())
                    .Where(c => c.ItemId.HasValue)
                    .Select(c => c.ItemId.Value))
            });
        }

        return result;
    }

    /// <summary>
    /// 给复用来的同名合集补写 MetaTube pid，落库口径与 OrganizeMetadataTask 一致（MetadataEdit）。
    /// </summary>
    public static async Task EnsureCollectionPidAsync(this ILibraryManager manager, Guid collectionId,
        string provider, string actorId, ILogger logger, CancellationToken cancellationToken)
    {
        var item = manager.GetItemById(collectionId);
        if (item is null)
        {
            logger.Warn("Collection not found when writing pid: {0}", collectionId);
            return;
        }

        item.SetPid(Plugin.ProviderId, provider, actorId);
        await manager.UpdateItemAsync(item, item, ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
        logger.Info("Write actor pid to collection: {0} [{1}]", item.Name, $"{provider}:{actorId}");
    }

    #endregion
}

#pragma warning restore CA2254
#endif
