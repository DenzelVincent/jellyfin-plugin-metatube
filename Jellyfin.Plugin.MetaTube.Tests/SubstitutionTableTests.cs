using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 系列译文对照表（阶段 C）直接复用 SubstitutionTable 的存储格式，
/// 这里钉住它的解析语义：C2 的表读取依赖「右侧留空 = 空串」「无等号 = null」这两种形态。
/// </summary>
public class SubstitutionTableTests
{
    [Fact]
    public void Parse_TrimsKeyAndValue()
    {
        var table = SubstitutionTable.Parse("  系列原名 = 译文名称  ");
        Assert.Equal("译文名称", table["系列原名"]);
    }

    [Fact]
    public void Parse_EmptyValueMeansDelete()
    {
        var table = SubstitutionTable.Parse("系列原名=");
        Assert.True(table.ContainsKey("系列原名"));
        Assert.Equal(string.Empty, table["系列原名"]);
    }

    [Fact]
    public void Parse_LineWithoutEqualSignStoresNull()
    {
        var table = SubstitutionTable.Parse("系列原名");
        Assert.True(table.ContainsKey("系列原名"));
        Assert.Null(table["系列原名"]);
    }

    [Fact]
    public void Parse_SkipsBlankLinesAndBlankKeys()
    {
        var table = SubstitutionTable.Parse("\n\n  \n=译文\nA=B\n");
        Assert.Single(table);
        Assert.Equal("B", table["A"]);
    }

    [Fact]
    public void ToString_RoundTripsThroughParse()
    {
        var table = SubstitutionTable.Parse("A=B\nC=D");
        var again = SubstitutionTable.Parse(table.ToString());
        Assert.Equal("B", again["A"]);
        Assert.Equal("D", again["C"]);
    }
}
