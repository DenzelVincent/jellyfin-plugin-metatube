using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 把 DMM 老 CDN 的封面地址换成新 CDN 的高清版本（上游 issue 653）。
///
/// JavBus / JAV321 / FANZA 的 <c>cover_url</c> 实际指向 <c>pics.dmm.co.jp</c> 的 800×538 转存图，
/// 同一作品在 <c>awsimgsrc.dmm.co.jp</c> 上有 2184×1468 的版本，文件名同为 <c>pl</c>，只是 CDN 和路径不同。
/// Jellyfin 海报位按 2:3 裁切，800 宽的横图裁完只剩 358×538；高清版裁完还有 978×1468。
///
/// 只做纯字符串改写：cid 从地址里取，取不到就返回 null 让调用方保持原样；
/// 个别老作品新 CDN 也只有 800px，但仍是同一张图，不影响可用性。
/// </summary>
public static class DmmCoverRewrite
{
    /// <summary>
    /// 两种 CDN 的路径里都带作品 cid；文件名必须恰好是 <c>&lt;cid&gt;pl.jpg</c>（海报），
    /// 这样剧照（<c>jp-*.jpg</c>）、缩略图（<c>ps.jpg</c>）之类不会被误换成海报。
    /// </summary>
    private static readonly Regex CidRegex = new(
        @"https?://(?:pics\.dmm\.co\.jp/digital/video|awsimgsrc\.dmm\.co\.jp/pics_dig/digital/video)/([a-z0-9_]+)/\1pl\.jpg",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// 返回高清封面地址；不是 DMM 图床或取不到 cid 时返回 <c>null</c>（调用方沿用原地址）。
    /// 已经指向新 CDN 的地址会原样返回（幂等）。
    /// </summary>
    public static string TryGetHdUrl(string coverUrl)
    {
        if (string.IsNullOrWhiteSpace(coverUrl))
            return null;

        var match = CidRegex.Match(coverUrl);
        if (!match.Success)
            return null;

        var cid = match.Groups[1].Value;
        return $"https://awsimgsrc.dmm.co.jp/pics_dig/digital/video/{cid}/{cid}pl.jpg";
    }
}
