#if !__EMBY__
using System.Globalization;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 让演员合集的主图每轮对齐该演员 Person 条目当前的主图（用户口径：以演员的图为准）。
///
/// 为什么不能只靠 <see cref="Providers.CollectionImageProvider"/>：它是 IRemoteImageProvider，
/// 只负责「供图」，服务端自动抓取时从演员相册里选哪一张不可控（相册混着横屏影片截图），
/// 这正是「刚开始是演员图、过一会儿变成截图」的来源之一。Person 条目那张就是演员页显示的那张。
///
/// 为什么复制而不是直接引用 Person 的文件：Jellyfin 换图时会删掉条目原来的本地图片
/// （v12.4 ImageSaver 的删除条件包含「旧图位于 InternalMetadataPath 下」），
/// 合集若指向 <c>/config/metadata/People/...</c> 的头像，用户给合集换一次图就会连演员头像一起删掉。
/// </summary>
internal static class CollectionCoverSync
{
    /// <summary>记录这张合集封面是按哪个源文件复制的，用于幂等跳过。</summary>
    private const string StampFileName = ".metatube-cover";

    /// <summary>
    /// 对齐一张合集的主图。返回是否真的写了盘。
    /// 单个合集失败只记日志，不把整轮任务打断（与 ApplyAsync 其它写入口径一致）。
    /// </summary>
    public static async Task<bool> AlignAsync(BoxSet collection, string actorName, string actorProvider,
        string actorId, ILibraryManager libraryManager, IProviderManager providerManager, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (collection is null || string.IsNullOrWhiteSpace(collection.Path))
        {
            logger.LogDebug("Skip collection cover: no folder path ({0})", actorName);
            return false;
        }

        var current = collection.GetImageInfo(ImageType.Primary, 0)?.Path;
        var stampPath = Path.Combine(collection.Path, StampFileName);
        var sourcePath = GetPersonImagePath(libraryManager, actorName);

        // Person 有本地主图时按「是否与本图一致」判；没有主图时退回旧口径，只要有可用本地封面就不动，
        // 避免每天把同一张相册图重复下载一遍。
        var upToDate = sourcePath is not null
            ? MatchesStamp(stampPath, sourcePath, current)
            : HasUsableLocalCover(current);

        switch (ActorCollectionSync.PlanCover(sourcePath is not null, upToDate))
        {
            case ActorCollectionSync.CoverAction.Skip:
                return false;

            case ActorCollectionSync.CoverAction.CopyPersonImage:
                return await CopyLocalAsync(collection, sourcePath, stampPath, providerManager, libraryManager,
                    logger, cancellationToken).ConfigureAwait(false);

            default:
                return await FetchGalleryAsync(collection, actorProvider, actorId, stampPath, providerManager,
                    libraryManager, logger, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string GetPersonImagePath(ILibraryManager libraryManager, string actorName)
    {
        if (string.IsNullOrWhiteSpace(actorName))
            return null;

        var path = libraryManager.GetPerson(actorName)?.GetImageInfo(ImageType.Primary, 0)?.Path;
        if (string.IsNullOrWhiteSpace(path) || PrimaryImageStore.IsRemotePath(path) || !File.Exists(path))
            return null;

        return path;
    }

    private static bool HasUsableLocalCover(string current)
    {
        return !string.IsNullOrWhiteSpace(current) &&
               !PrimaryImageStore.IsRemotePath(current) &&
               File.Exists(current);
    }

    /// <summary>
    /// 指纹四段：来源类型、来源（文件路径或相册图地址）、源文件修改时间、上次写出的封面路径。
    /// 任一段对不上就重写——演员换了头像（时间变）、用户给合集手选了别的图（路径变）都会触发对齐。
    /// </summary>
    private static bool MatchesStamp(string stampPath, string sourcePath, string current)
    {
        if (!HasUsableLocalCover(current) || !File.Exists(stampPath))
            return false;

        try
        {
            var lines = File.ReadAllLines(stampPath);
            return lines.Length >= 4 &&
                   lines[0] == "person" &&
                   string.Equals(lines[1], sourcePath, StringComparison.Ordinal) &&
                   string.Equals(lines[2], SourceTicks(sourcePath), StringComparison.Ordinal) &&
                   string.Equals(lines[3], current, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            // 指纹读不出来就当没对齐，重写一次即可，不影响正确性。
            return false;
        }
    }

    private static async Task<bool> CopyLocalAsync(BoxSet collection, string sourcePath, string stampPath,
        IProviderManager providerManager, ILibraryManager libraryManager, ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(sourcePath);
            await providerManager.SaveImage(collection, stream, GetMimeType(sourcePath), ImageType.Primary, 0,
                cancellationToken).ConfigureAwait(false);

            await libraryManager
                .UpdateItemAsync(collection, collection, ItemUpdateType.ImageUpdate, cancellationToken)
                .ConfigureAwait(false);

            var dest = collection.GetImageInfo(ImageType.Primary, 0)?.Path;
            WriteStamp(stampPath, "person", sourcePath, SourceTicks(sourcePath), dest);
            logger.LogInformation("Align collection cover to actor image: {0} -> {1}", collection.Name, dest);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning("Align collection cover failed: {0} ({1})", collection.Name, e.Message);
            return false;
        }
    }

    /// <summary>演员没有 Person 头像时的回落：只在合集缺图时补相册第一张。</summary>
    private static async Task<bool> FetchGalleryAsync(BoxSet collection, string actorProvider, string actorId,
        string stampPath, IProviderManager providerManager, ILibraryManager libraryManager, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(actorProvider) || string.IsNullOrWhiteSpace(actorId))
        {
            logger.LogDebug("Collection cover fallback skipped, no actor pid: {0}", collection.Name);
            return false;
        }

        try
        {
            var actorInfo = await ApiClient.GetActorInfoAsync(actorProvider, actorId, cancellationToken)
                .ConfigureAwait(false);
            if (actorInfo?.Images?.Any() != true)
            {
                logger.LogDebug("Collection cover fallback skipped, actor has no image: {0}", collection.Name);
                return false;
            }

            // 取位与 ActorImageProvider 一致（0.5），同一张图在两处看到的效果才对得上。
            var url = ApiClient.GetPrimaryImageApiUrl(actorInfo.Provider, actorInfo.Id, actorInfo.Images[0], 0.5, true);
            var (data, error) = await PrimaryImageStore.DownloadAsync(url, cancellationToken).ConfigureAwait(false);
            if (data is null)
            {
                logger.LogDebug("Collection cover fallback download failed: {0} ({1})", collection.Name, error);
                return false;
            }

            using var stream = new MemoryStream(data);
            await providerManager.SaveImage(collection, stream, DetectMimeType(data), ImageType.Primary, 0,
                cancellationToken).ConfigureAwait(false);

            await libraryManager
                .UpdateItemAsync(collection, collection, ItemUpdateType.ImageUpdate, cancellationToken)
                .ConfigureAwait(false);

            var dest = collection.GetImageInfo(ImageType.Primary, 0)?.Path;
            WriteStamp(stampPath, "gallery", url, "0", dest);
            logger.LogInformation("Fill collection cover from actor gallery: {0} -> {1}", collection.Name, dest);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception e)
        {
            logger.LogWarning("Fill collection cover failed: {0} ({1})", collection.Name, e.Message);
            return false;
        }
    }

    private static void WriteStamp(string stampPath, string kind, string source, string ticks, string dest)
    {
        try
        {
            File.WriteAllLines(stampPath, new[] { kind, source ?? string.Empty, ticks ?? "0", dest ?? string.Empty });
        }
        catch (Exception)
        {
            // 指纹只是加速幂等；写不下（只读挂载等）下轮重写一次封面，不影响结果。
        }
    }

    private static string SourceTicks(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception)
        {
            return "0";
        }
    }

    private static string GetMimeType(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".gif" => "image/gif",
            ".webp" => "image/webp",
            _ => "image/jpeg"
        };
    }

    /// <summary>DownloadAsync 已校验过确实是图片，这里按文件头给出对应 MIME，SaveImage 靠它决定扩展名。</summary>
    private static string DetectMimeType(byte[] data)
    {
        if (data.Length >= 4 && data[0] == 0x89 && data[1] == 0x50)
            return "image/png";
        if (data.Length >= 4 && data[0] == 0x47 && data[1] == 0x49)
            return "image/gif";
        if (data.Length >= 4 && data[0] == 0x52 && data[1] == 0x49)
            return "image/webp";

        return "image/jpeg";
    }
}
#endif
