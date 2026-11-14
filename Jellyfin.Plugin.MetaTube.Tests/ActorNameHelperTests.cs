using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员名拆分：A（B）里 B 是搜索用艺名，A 进候选池；原串不删改。
/// 演员页名字选择：已有名字优先，空名用表艺名，最后退回演员接口名。
/// </summary>
public class ActorNameHelperTests
{
    [Theory]
    [InlineData("柚井初美（ゆい はつみ）", "柚井初美", "ゆい はつみ")]
    [InlineData("柚井初美(ゆい はつみ)", "柚井初美", "ゆい はつみ")]
    [InlineData("A（B）", "A", "B")]
    [InlineData("柚井初美", "柚井初美", "柚井初美")]
    [InlineData("  柚井初美  ", "柚井初美", "柚井初美")]
    public void Parse_SplitsDisplayAndSearchName(string raw, string display, string search)
    {
        var parts = ActorNameHelper.Parse(raw);

        Assert.Equal(display, parts.DisplayName);
        Assert.Equal(search, parts.SearchName);
    }

    [Theory]
    [InlineData("（ゆい はつみ）")]
    [InlineData("A（ゆい（はつみ））")]
    public void Parse_KeepsAmbiguousFormsIntact(string raw)
    {
        var parts = ActorNameHelper.Parse(raw);

        Assert.Equal(raw, parts.DisplayName);
        Assert.Equal(raw, parts.SearchName);
    }

    [Fact]
    public void ResolvePersonDisplay_KeepsExistingName()
    {
        Assert.Equal("下川紬", ActorNameHelper.ResolvePersonDisplay("下川紬", "ゆい はつみ", "柚井初美"));
    }

    [Fact]
    public void ResolvePersonDisplay_UsesStageWhenExistingEmpty()
    {
        Assert.Equal("柚井初美", ActorNameHelper.ResolvePersonDisplay("  ", "ゆい はつみ", "柚井初美"));
    }

    [Fact]
    public void ResolvePersonDisplay_TrimsStage()
    {
        Assert.Equal("柚井初美", ActorNameHelper.ResolvePersonDisplay(null, "x", " 柚井初美 "));
    }

    [Fact]
    public void ResolvePersonDisplay_FallsBackToInterfaceName()
    {
        Assert.Equal("interface-name", ActorNameHelper.ResolvePersonDisplay(null, "interface-name", null));
        Assert.Equal("interface-name", ActorNameHelper.ResolvePersonDisplay(null, "interface-name", "   "));
    }
}
