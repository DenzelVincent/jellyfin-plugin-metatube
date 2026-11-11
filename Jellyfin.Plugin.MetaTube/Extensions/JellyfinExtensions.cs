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

    /// <summary>
    /// 影片是否带 MetaTube pid。
    /// 不能用 InternalItemsQuery.HasAnyProviderId 过滤：库里历史数据的 provider key 有
    /// "MetaTube" / "metatube" 两种写法，数据库查询是大小写敏感的，会漏掉另一半影片；
    /// 内存里的 ProviderIds 是 OrdinalIgnoreCase，这里统一按内存口径判断。
    /// </summary>
    public static bool HasMetaTubePid(this BaseItem item)
    {
        var pid = item.GetPid(Plugin.ProviderId);
        return !string.IsNullOrEmpty(pid.Provider) && !string.IsNullOrEmpty(pid.Id);
    }

    /// <summary>
    /// 数据库筛片用的 provider 过滤：历史数据有 "MetaTube" / "metatube" 两种写法，
    /// 数据库查询区分大小写，必须两种都带上。不要退回「查全部影片再按内存 ProviderIds 过滤」——
    /// GetItemList 在不带用户/字段过滤的调用方式下不一定返回全库影片。
    /// </summary>
    public static Dictionary<string, string> BuildMetaTubeProviderFilter()
    {
        return new Dictionary<string, string>
        {
            { Plugin.ProviderId, string.Empty },
            { Plugin.ProviderId.ToLowerInvariant(), string.Empty }
        };
    }

    #endregion

    #region Collection

    /// <summary>
    /// 取影片的演员清单。这里只用作「本次扫描里出现了哪些演员」——
    /// 演职行不存演员 pid（服务端读它时只映射 Id/Name/Role/SortOrder/Type），
    /// 所以 pid 一律另按演员从 Person 实体取，成员也另按演职表查，见下面两个方法。
    /// </summary>
    public static IReadOnlyList<PersonInfo> GetActors(this ILibraryManager manager, BaseItem item)
    {
        return manager.GetPeople(item).Where(p => p.Type == PersonKind.Actor).ToList();
    }

    /// <summary>
    /// 「这位演员参演了哪些影片」，与服务端演员详情页同一口径：直接按演职表取，
    /// 不要求影片带 MetaTube pid。按本次扫描的影片算成员会漏片（演员页 4 部、合集里只有 1 部即由此而来）。
    /// 用 InternalItemsQuery.Person（演职名精确匹配，v10.11 BaseItemRepository.cs:1891、
    /// v12.4 BaseItemRepository.TranslateQuery.cs:461）而不用 PersonIds：后者要先有一条同名同写法的
    /// Person 条目（它内部还是按 BaseItems.Name→Peoples.Name 联表），条目缺失时静默返回空集。
    /// 名字必须是库里的原始写法（ActorGroup.CreditNames），这里比较是二进制精确的，规范化过就查不到。
    /// libraryScope 非空时只查这些媒体库：否则白名单外库里的同名演员影片会被当成成员重新加回合集。
    /// </summary>
    public static HashSet<Guid> GetActorMovieIds(this ILibraryManager manager, IEnumerable<string> creditNames,
        IReadOnlyCollection<Guid> libraryScope = null)
    {
        var scope = libraryScope is { Count: > 0 } ? libraryScope.ToArray() : null;
        var ids = new HashSet<Guid>();
        foreach (var name in creditNames.Where(n => !string.IsNullOrEmpty(n)))
        {
            var query = new InternalItemsQuery
            {
                Person = name,
                MediaTypes = new[] { MediaType.Video },
                IncludeItemTypes = new[] { BaseItemKind.Movie }
            };
            // 白名单下推给 DB：按演员取全量成员与本次扫描必须同一口径。
            if (scope is not null)
                query.AncestorIds = scope;

            foreach (var item in manager.GetItemList(query))
                ids.Add(item.Id);
        }

        return ids;
    }

    /// <summary>
    /// 影片是否落在演员合集的媒体库白名单内。空范围 = 全部库（保持旧行为）。
    /// GetCollectionFolders 是服务端判断「条目属于哪个媒体库」的标准入口，
    /// 链接条目可能同时属于多个库，任一所属库命中即视为在范围内。
    /// </summary>
    public static bool IsInActorCollectionScope(this ILibraryManager manager, BaseItem item,
        IReadOnlyCollection<Guid> libraryScope)
    {
        if (libraryScope is null || libraryScope.Count == 0)
            return true;

        return ActorCollectionScope.Includes(libraryScope,
            manager.GetCollectionFolders(item).Select(folder => folder.Id));
    }

    /// <summary>
    /// 演员 pid 的第一级：读 Person 实体。全库里只有这条 Item 能持久保存演员 pid——
    /// 刮削影片时 MovieProvider 写在 PersonInfo 上的 pid，由服务端 SavePeopleMetadataAsync
    /// （v10.11 LibraryManager.cs:3106-3113、v12.4 :3896-3905）抄进 Person 条目入库；
    /// 演员详情页被单独刮过时 ActorProvider 也直接写它。
    /// Person 条目可能还不存在（演员从没刮过），也可能被 People 校验任务的清理环节删掉，此时给回空 pid。
    /// </summary>
    public static ProviderId ReadActorPid(this ILibraryManager manager, string displayName)
    {
        var person = manager.GetPerson(displayName);
        return person is null ? ProviderId.Parse(null) : person.GetPid(Plugin.ProviderId);
    }

    /// <summary>
    /// 把现搜到的演员 pid 落回 Person 实体，下一级任务才不必再搜一遍。
    /// 口径与 EnsureCollectionPidAsync 相同（MetadataEdit 回写），UpdateItemAsync 对 Person 条目同样会把
    /// ProviderIds 整体重写入库（v10.11 LibraryManager.cs:2183、v12.4 :2762，无类型门槛）。
    /// Person 条目还不存在时无从写起：10.11 SDK 上没有 GetOrCreatePerson，插件自己造条目会绕过服务端的
    /// 演职关联，只能等下次刮削由 SavePeopleMetadataAsync 建出来，这一轮先把 pid 用在合集上。
    /// </summary>
    public static async Task WriteActorPidAsync(this ILibraryManager manager, string displayName, string provider,
        string actorId, ILogger logger, CancellationToken cancellationToken)
    {
        var person = manager.GetPerson(displayName);
        if (person is null)
        {
            logger.Debug("No Person item to write actor pid: {0}", displayName);
            return;
        }

        person.SetPid(Plugin.ProviderId, provider, actorId);
        await manager.UpdateItemAsync(person, person, ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
        logger.Info("Write actor pid to person item: {0} [{1}]", displayName, $"{provider}:{actorId}");
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
                // 本仓库没开可空注解，服务端这里可能给回 null（与 ActorCollectionSync 里同一条口径），
                // 按空集处理即「成员为空」，不能让整个扫描抛 NullReferenceException。
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

    /// <summary>
    /// 合集内的存活成员数：LinkedChildren 的外键是 NoAction，影片删掉后链接还留着，
    /// 所以不能直接取链接条数。按去重后的成员 id 做一次批量查询，回来的才是真存在的。
    /// 查询不带用户过滤，统计的是库里真实存在的成员数；受限用户看到的角标可能比它小。
    /// </summary>
    public static int CountLivingMembers(this ILibraryManager manager, BoxSet box)
    {
        var ids = (box.LinkedChildren ?? Array.Empty<LinkedChild>())
            .Where(child => child.ItemId.HasValue)
            .Select(child => child.ItemId.Value)
            .Distinct()
            .ToArray();

        return ids.Length == 0 ? 0 : manager.GetItemList(new InternalItemsQuery { ItemIds = ids }).Count;
    }

    /// <summary>
    /// 值没变就不落库。两边都是 null 也算一致，否则关掉开关后每轮还会各写一次空值。
    /// 纯判据，便于离线测试。
    /// </summary>
    public static bool NeedsCountRatingWrite(float? current, float? target)
    {
        return current != target;
    }

    /// <summary>
    /// 按 EnableCollectionCountRating 对齐合集上的片数：开着写存活片数，关着清成 null。
    /// 每次调用现读配置，改开关不用等重启；合集刚被删掉时取不到条目，直接返回。
    /// </summary>
    public static async Task SyncCollectionCountRatingAsync(this ILibraryManager manager, Guid collectionId,
        ILogger logger, CancellationToken cancellationToken)
    {
        if (manager.GetItemById(collectionId) is not BoxSet box)
            return;

        float? target = null;
        if (Plugin.Instance.Configuration.EnableCollectionCountRating)
            target = manager.CountLivingMembers(box);

        if (!NeedsCountRatingWrite(box.CommunityRating, target))
            return;

        box.CommunityRating = target;
        await manager.UpdateItemAsync(box, box, ItemUpdateType.MetadataEdit, cancellationToken)
            .ConfigureAwait(false);
        logger.Debug("Collection count rating: {0} -> {1}", box.Name,
            target.HasValue ? target.Value.ToString("0") : "null");
    }

    #endregion
}

#pragma warning restore CA2254
#endif
