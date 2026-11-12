using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员名拆分：A（B）里 B 是搜索用艺名，A 是可能已转好的汉字名；原串不删改。
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

    [Theory]
    [InlineData("ゆい はつみ", "柚井初美", "柚井初美（ゆい はつみ）")]
    [InlineData("ゆい はつみ", "", "ゆい はつみ")]
    [InlineData("ゆい はつみ", "ゆい はつみ", "ゆい はつみ")]
    [InlineData("ゆい はつみ", "柚井初美（ゆい はつみ）", "柚井初美（ゆい はつみ）")]
    [InlineData("", "柚井初美", "柚井初美")]
    public void FormatDisplay_ShowsTranslationWithStage(string stage, string translated, string expected)
    {
        Assert.Equal(expected, ActorNameHelper.FormatDisplay(stage, translated));
    }

    [Fact]
    public void ResolvePersonDisplay_KeepsExistingName()
    {
        Assert.Equal("下川紬", ActorNameHelper.ResolvePersonDisplay(
            "下川紬", "ゆい はつみ", "ゆい はつみ", "下川綢", true));
    }

    [Fact]
    public void ResolvePersonDisplay_UsesComposedDictionaryNameWhenExistingEmpty()
    {
        Assert.Equal("下川紬（ゆい はつみ）", ActorNameHelper.ResolvePersonDisplay(
            "  ", "ゆい はつみ", "ゆい はつみ", "下川紬", true));
    }

    [Fact]
    public void ResolvePersonDisplay_UsesStageWhenTranslationOff()
    {
        Assert.Equal("ゆい はつみ", ActorNameHelper.ResolvePersonDisplay(
            null, "interface-name", "ゆい はつみ", "下川紬", false));
    }

    [Fact]
    public void ResolvePersonDisplay_FallsBackToInterfaceName()
    {
        Assert.Equal("interface-name", ActorNameHelper.ResolvePersonDisplay(
            null, "interface-name", null, null, true));
    }

    [Fact]
    public void ResolveTranslatedCollisions_KeepsOriginalForLaterDuplicate()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "B" }, new[] { "X", "X" });

        Assert.Equal(new[] { "X", "B" }, result);
    }

    [Fact]
    public void ResolveTranslatedCollisions_KeepsDistinctTranslations()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "B" }, new[] { "X", "Y" });

        Assert.Equal(new[] { "X", "Y" }, result);
    }

    [Fact]
    public void ResolveTranslatedCollisions_FallsBackWhenTranslationEqualsOwnOriginal()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "B" }, new[] { "A", "A" });

        Assert.Equal(new[] { "A", "B" }, result);
    }

    [Fact]
    public void ResolveTranslatedCollisions_LeavesUnavoidableMerge()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "X" }, new[] { "X", "X" });

        Assert.Equal(new[] { "X", "X" }, result);
    }
}
