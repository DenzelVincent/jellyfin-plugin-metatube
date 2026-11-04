#if !__EMBY__
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 清掉没有任何存活成员的合集（用户口径：不区分是不是演员合集）。
///
/// 12.4 的合集成员存在 LinkedChildren 里，而 LinkedChildEntity 的两个外键都是
/// <c>DeleteBehavior.NoAction</c>：影片从库里删掉后，那条链接不会跟着消失。
/// 所以「空」不能只看链接条数，要逐个确认条目是否还在。
/// </summary>
public static class EmptyCollectionCleaner
{
    /// <summary>有没有至少一个成员还在库里。纯判据，便于离线测试。</summary>
    public static bool HasLivingMember(IEnumerable<Guid> memberIds, Func<Guid, bool> exists)
    {
        if (memberIds is null || exists is null)
            return false;

        return memberIds.Any(exists);
    }

    /// <summary>扫描全部合集，删除一个存活成员都没有的合集；返回删除数量。</summary>
    public static int RemoveAll(ILibraryManager libraryManager, ILogger logger)
    {
        var removed = 0;
        foreach (var box in libraryManager.GetItemList(new InternalItemsQuery
                 {
                     IncludeItemTypes = new[] { BaseItemKind.BoxSet }
                 }).OfType<BoxSet>())
        {
            var memberIds = (box.LinkedChildren ?? Array.Empty<LinkedChild>())
                .Where(child => child.ItemId.HasValue)
                .Select(child => child.ItemId.Value)
                .ToList();

            if (HasLivingMember(memberIds, id => libraryManager.GetItemById(id) is not null))
                continue;

            if (libraryManager.GetItemById(box.Id) is not BaseItem item)
                continue;

            // 与 Jellyfin 自己「删除合集」一致：条目与合集目录一起删，否则空目录会被下次扫描捡回来。
            libraryManager.DeleteItem(item, new DeleteOptions { DeleteFileLocation = true });
            logger.LogInformation("Remove empty collection: {0} ({1})", box.Name, box.Id);
            removed++;
        }

        return removed;
    }
}
#endif
