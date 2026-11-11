using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员名拆分与角色分类：括号内是搜索更准确的写法，括号外是显示用的汉字名；
/// 原始字符串不删改，拆分只用于展示、搜索与入表。
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
    [InlineData("ゆい はつみ", "kana")]
    [InlineData("アイカ", "kana")]
    [InlineData("Yui Hatsumi", "romaji")]
    [InlineData("AIKA", "romaji")]
    [InlineData("柚井初美", "stage")]
    [InlineData("", "alias")]
    public void Classify_DetectsNameRole(string name, string role)
    {
        Assert.Equal(role, ActorNameHelper.Classify(name));
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
