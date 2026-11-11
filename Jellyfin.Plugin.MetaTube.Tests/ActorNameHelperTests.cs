using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员名规范化：来源会把读音写在名字后的括号里（柚井初美（ゆい はつみ））。
/// 只去掉读音注解，括号里是汉字时必须原样保留，避免把别名/组合当成读音删掉。
/// </summary>
public class ActorNameHelperTests
{
    [Theory]
    [InlineData("柚井初美（ゆい はつみ）", "柚井初美")]
    [InlineData("柚井初美(ゆい はつみ)", "柚井初美")]
    [InlineData("Yui Hatsumi（ゆい はつみ）", "Yui Hatsumi")]
    [InlineData("AIKA（アイカ）", "AIKA")]
    [InlineData("A (B)", "A")]
    [InlineData("  柚井初美（ゆい はつみ）  ", "柚井初美")]
    [InlineData("柚井初美", "柚井初美")]
    public void Normalize_StripsTrailingReading(string input, string expected)
    {
        Assert.Equal(expected, ActorNameHelper.Normalize(input));
    }

    [Theory]
    [InlineData("A（B組）")]
    [InlineData("柚井初美（ゆい はつみ）（別名）")]
    [InlineData("A（ゆい（はつみ））")]
    [InlineData("（ゆい はつみ）")]
    public void Normalize_KeepsNonReadingAnnotations(string input)
    {
        Assert.Equal(input, ActorNameHelper.Normalize(input));
    }

    [Fact]
    public void Normalize_HandlesNullAndBlank()
    {
        Assert.Null(ActorNameHelper.Normalize(null));
        Assert.Equal("   ", ActorNameHelper.Normalize("   "));
    }

    [Fact]
    public void NormalizeAll_ReturnsNullForNull()
    {
        Assert.Null(ActorNameHelper.NormalizeAll(null));
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
    public void ResolveTranslatedCollisions_HandlesSwappedNames()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "B" }, new[] { "B", "A" });

        Assert.Equal(new[] { "B", "A" }, result);
    }

    [Fact]
    public void ResolveTranslatedCollisions_FallsBackWhenTranslationEqualsOwnOriginal()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "B" }, new[] { "A", "A" });

        Assert.Equal(new[] { "A", "B" }, result);
    }

    [Fact]
    public void ResolveTranslatedCollisions_HandlesMiddleCollision()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "B", "C" }, new[] { "X", "X", "Y" });

        Assert.Equal(new[] { "X", "B", "Y" }, result);
    }

    [Fact]
    public void ResolveTranslatedCollisions_LeavesUnavoidableMerge()
    {
        var result = ActorNameHelper.ResolveTranslatedCollisions(
            new[] { "A", "X" }, new[] { "X", "X" });

        Assert.Equal(new[] { "X", "X" }, result);
    }
}
