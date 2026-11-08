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

    [Fact]
    public void BuildMetaTubeProviderFilter_IncludesBothHistoricalCasings()
    {
        var filter = JellyfinExtensions.BuildMetaTubeProviderFilter();

        Assert.Contains("MetaTube", filter.Keys);
        Assert.Contains("metatube", filter.Keys);
    }

    /// <summary>
    /// 片数写入的落库判据：值没变就不写，尤其「两边都是 null」不能算需要写，
    /// 否则关掉开关后每轮还会给每个合集各补一次空值更新。
    /// </summary>
    [Fact]
    public void NeedsCountRatingWrite_FalseWhenBothValuesAreNull()
    {
        Assert.False(JellyfinExtensions.NeedsCountRatingWrite(null, null));
    }

    [Fact]
    public void NeedsCountRatingWrite_TrueWhenValueAppearsOrDisappears()
    {
        Assert.True(JellyfinExtensions.NeedsCountRatingWrite(null, 12));
        Assert.True(JellyfinExtensions.NeedsCountRatingWrite(12, null));
        Assert.True(JellyfinExtensions.NeedsCountRatingWrite(12, 13));
    }

    [Fact]
    public void NeedsCountRatingWrite_FalseWhenCountUnchanged()
    {
        Assert.False(JellyfinExtensions.NeedsCountRatingWrite(12, 12));
        Assert.False(JellyfinExtensions.NeedsCountRatingWrite(0, 0));
    }
}
