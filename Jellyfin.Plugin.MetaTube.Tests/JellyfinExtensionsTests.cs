using Jellyfin.Plugin.MetaTube.Extensions;
using MediaBrowser.Controller.Entities.Movies;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 全库任务的历史数据兼容：库里 provider key 有 "MetaTube"/"metatube" 两种写法，
/// 内存 ProviderIds 是 OrdinalIgnoreCase，任务必须按这个口径筛片，不能再退回大小写敏感的 DB 查询。
/// </summary>
public class JellyfinExtensionsTests
{
    [Fact]
    public void HasMetaTubePid_RecognizesLowercaseHistoryKey()
    {
        var movie = new Movie();
        movie.ProviderIds["metatube"] = "javbus:1";

        Assert.True(movie.HasMetaTubePid());
    }

    [Fact]
    public void HasMetaTubePid_RejectsMissingOrEmptyPid()
    {
        var movie = new Movie();
        Assert.False(movie.HasMetaTubePid());

        movie.ProviderIds["MetaTube"] = string.Empty;
        Assert.False(movie.HasMetaTubePid());
    }
}
