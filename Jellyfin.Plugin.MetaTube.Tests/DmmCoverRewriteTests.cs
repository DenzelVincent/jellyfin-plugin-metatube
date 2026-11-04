using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// DMM 高清封面改写（上游 issue 653）的离线防线：只认海报文件，取不到 cid 一律保持原样。
/// </summary>
public class DmmCoverRewriteTests
{
    [Fact]
    // JavBus/JAV321 的 cover_url 指向老 CDN，改写成新 CDN 的同一作品高清版。
    public void TryGetHdUrl_RewritesOldCdnCover()
    {
        Assert.Equal(
            "https://awsimgsrc.dmm.co.jp/pics_dig/digital/video/abc01234/abc01234pl.jpg",
            DmmCoverRewrite.TryGetHdUrl("https://pics.dmm.co.jp/digital/video/abc01234/abc01234pl.jpg"));
    }

    [Fact]
    // 已经在新 CDN 上：结果不变（幂等），便于调用方无条件替换。
    public void TryGetHdUrl_KeepsNewCdnCover()
    {
        const string url = "https://awsimgsrc.dmm.co.jp/pics_dig/digital/video/abc01234/abc01234pl.jpg";
        Assert.Equal(url, DmmCoverRewrite.TryGetHdUrl(url));
    }

    [Fact]
    // 缩略图 ps.jpg 不是海报，换成 pl 会悄悄改变图片含义，必须保持原样。
    public void TryGetHdUrl_IgnoresThumbFile()
    {
        Assert.Null(DmmCoverRewrite.TryGetHdUrl("https://pics.dmm.co.jp/digital/video/abc01234/abc01234ps.jpg"));
    }

    [Fact]
    // 剧照 jp-*.jpg 同理，不参与改写。
    public void TryGetHdUrl_IgnoresPreviewFile()
    {
        Assert.Null(DmmCoverRewrite.TryGetHdUrl("https://pics.dmm.co.jp/digital/video/abc01234/abc01234jp-1.jpg"));
    }

    [Fact]
    // 非 DMM 图床（DUGA/DAHLIA 等自家 CDN）没有 cid，原样返回。
    public void TryGetHdUrl_IgnoresOtherHosts()
    {
        Assert.Null(DmmCoverRewrite.TryGetHdUrl("https://example.com/covers/abc01234.jpg"));
    }

    [Fact]
    public void TryGetHdUrl_ToleratesBlankInput()
    {
        Assert.Null(DmmCoverRewrite.TryGetHdUrl(null));
        Assert.Null(DmmCoverRewrite.TryGetHdUrl("  "));
    }
}
