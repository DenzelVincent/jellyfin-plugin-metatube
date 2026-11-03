#if !__EMBY__
using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.Providers;

/// <summary>
/// 演员合集的封面：合集上的 MetaTube pid 指向演员，于是取该演员在 MetaTube 的照片作 Primary。
/// 只有带 pid 的 BoxSet 才声明支持，用户手建的合集不受影响。
/// 这里不按 EnableActorCollections 门控——关开关后既有合集仍可换封面（设计 4.3 不删合集）。
/// </summary>
public class CollectionImageProvider : BaseProvider, IRemoteImageProvider, IHasOrder
{
    public CollectionImageProvider(ILogger<CollectionImageProvider> logger) : base(logger)
    {
    }

    public bool Supports(BaseItem item)
    {
        if (item is not BoxSet)
            return false;

        var pid = item.GetPid(Plugin.ProviderId);
        return !string.IsNullOrWhiteSpace(pid.Id) && !string.IsNullOrWhiteSpace(pid.Provider);
    }

    public IEnumerable<ImageType> GetSupportedImages(BaseItem item)
    {
        return new List<ImageType>
        {
            ImageType.Primary
        };
    }

    public async Task<IEnumerable<RemoteImageInfo>> GetImages(BaseItem item, CancellationToken cancellationToken)
    {
        var pid = item.GetPid(Plugin.ProviderId);
        if (string.IsNullOrWhiteSpace(pid.Id) || string.IsNullOrWhiteSpace(pid.Provider))
            return Enumerable.Empty<RemoteImageInfo>();

        var actorInfo = await ApiClient.GetActorInfoAsync(pid.Provider, pid.Id, cancellationToken);

        if (actorInfo?.Images?.Any() != true)
            return Enumerable.Empty<RemoteImageInfo>();

        return actorInfo.Images.Select(image => new RemoteImageInfo
        {
            ProviderName = Name,
            Type = ImageType.Primary,
            Url = ApiClient.GetPrimaryImageApiUrl(actorInfo.Provider, actorInfo.Id, image, 0.66, true)
        });
    }
}
#endif
