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

        try
        {
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
        catch (OperationCanceledException)
        {
            // 用户取消或服务端超时下发取消时必须原样抛出，否则这一次找图会被当成「正常但没图」。
            throw;
        }
        catch (Exception ex)
        {
            // ApiClient.GetDataAsync 走的是普通 Exception（后端错误码 / data 为空 / 连不上都会抛），
            // 合集的 pid 可能指向早已下架的演员：记一条 Warn 交空集合，不让异常冲出去打断整条刮图链路。
            Logger.Warn("Fetch actor images for collection failed: {0} [{1}] {2}", item.Name,
                $"{pid.Provider}:{pid.Id}", ex.Message);
            return Enumerable.Empty<RemoteImageInfo>();
        }
    }
}
#endif
