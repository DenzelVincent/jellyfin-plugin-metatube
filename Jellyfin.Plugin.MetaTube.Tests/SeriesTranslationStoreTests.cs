using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 译文表的两条硬约束（设计 4.6）：整键匹配（绝不做子串替换）与回写幂等。
/// 合集靠名字匹配，表里一旦自相矛盾就会裂合集，所以这两条必须在离线阶段卡住。
/// </summary>
public class SeriesTranslationStoreTests
{
    [Fact]
    public void Lookup_HitReturnsTableValue()
    {
        Assert.True(SeriesTranslationStore.Lookup("系列原名=译文名称", "系列原名", out var target));
        Assert.Equal("译文名称", target);
    }

    [Fact]
    // 等号右侧留空是用户「这个系列就要保留原文」的显式意图，命中即不再调引擎。
    public void Lookup_EmptyTableValueIsStillAHit()
    {
        Assert.True(SeriesTranslationStore.Lookup("系列原名=", "系列原名", out var target));
        Assert.True(string.IsNullOrEmpty(target));
    }

    [Fact]
    // 没有等号的行按 SubstitutionTable 的既有语义存成 null，同样视为保留原文。
    public void Lookup_LineWithoutEqualSignHitsWithNull()
    {
        Assert.True(SeriesTranslationStore.Lookup("系列原名", "系列原名", out var target));
        Assert.Null(target);
    }

    [Fact]
    public void Lookup_MissReturnsFalse()
    {
        Assert.False(SeriesTranslationStore.Lookup("A=译文B", "系列原名", out var target));
        Assert.Null(target);
    }

    [Fact]
    // 必须是整键匹配：系列名之间普遍存在前缀包含，子串替换会误伤（设计 4.6 硬约束一）。
    public void Lookup_DoesNotMatchSubstrings()
    {
        Assert.False(SeriesTranslationStore.Lookup("系列=译文", "系列第二期", out _));
    }

    [Fact]
    public void Append_AddsRowAndKeepsExistingRows()
    {
        var raw = SeriesTranslationStore.Append("A=甲", "B", "乙");

        Assert.Contains("A=甲", raw);
        Assert.Contains("B=乙", raw);
        Assert.True(SeriesTranslationStore.Lookup(raw, "A", out _));
        Assert.True(SeriesTranslationStore.Lookup(raw, "B", out var target));
        Assert.Equal("乙", target);
    }

    [Fact]
    // 同一个系列被并发刮削多次时，重复回写必须不改变原串，否则配置会被无意义地反复落盘。
    public void Append_IsIdempotentForSameKeyAndValue()
    {
        Assert.Equal("A=甲", SeriesTranslationStore.Append("A=甲", "A", "甲"));
    }

    [Fact]
    // 覆盖旧译文是「引擎换译法/用户纠正误译」后唯一的收敛路径。
    public void Append_OverwritesSameKeyWithNewValue()
    {
        var raw = SeriesTranslationStore.Append("A=甲", "A", "甲A");

        Assert.Equal("甲A", SeriesTranslationStore.Lookup(raw, "A", out var target) ? target : null);
    }

    [Fact]
    // 已有的 `Append_IsIdempotentForSameKeyAndValue` 只有一行表，`Append` 里那条
    // 「键值相同就原样返回」的分支被 `table.ToString()` 重写整串也测不出来。
    // 这里用两行表钉死：命中已有键值对时**整串一字不改**，否则每张影片刮完都要重写一遍 config.xml。
    public void Append_LeavesMultiRowTableUntouched()
    {
        var raw = "A=甲\nB=乙";

        Assert.Equal(raw, SeriesTranslationStore.Append(raw, "B", "乙"));
        Assert.Equal(raw, SeriesTranslationStore.Append(raw, "a", "甲"));
    }

    [Fact]
    // 另一半：新键必须照常追加，不能把「不改写」做成一刀切。
    // 不比对整串顺序（Dictionary 的枚举顺序不是契约），只验条数与查得到。
    public void Append_AddsNewKeyToMultiRowTable()
    {
        var raw = SeriesTranslationStore.Append("A=甲\nB=乙", "C", "丙");

        Assert.Equal(3, raw.Split('\n').Length);
        Assert.True(SeriesTranslationStore.Lookup(raw, "C", out var target));
        Assert.Equal("丙", target);
    }

    [Fact]
    // 表里的键与值都是 Parse/ToString 裁过的（`SubstitutionTable.cs:18,39`），入表前不裁就会出现
    // 「写进去是 B=乙、拿 " B " 查永远 miss」，每张影片都白调一次引擎。
    public void Append_TrimsKeyAndValueBeforeStoring()
    {
        var raw = SeriesTranslationStore.Append("A=甲", " B ", " 乙 ");

        Assert.True(SeriesTranslationStore.Lookup(raw, "B", out var target));
        Assert.Equal("乙", target);
        Assert.Equal(raw, SeriesTranslationStore.Append(raw, "B", "乙"));
    }

    [Fact]
    // 表按行存：含换行的键或值会写出多余的行，下次 Parse 把它读成「没有等号 = 保留原文」的键，
    // 只能手改 config.xml 才清得掉。这种行必须整个拒收，原串一字不改。
    public void Append_RejectsLineBreaksInKeyOrValue()
    {
        Assert.Equal("A=甲", SeriesTranslationStore.Append("A=甲", "B\nC", "丁"));
        Assert.Equal("A=甲", SeriesTranslationStore.Append("A=甲", "B", "丁\n戊"));
        Assert.Equal("A=甲", SeriesTranslationStore.Append("A=甲", "B", "丁\r\n戊"));
    }

    [Fact]
    // 与 Append 的裁键对称：查表也要先裁，否则带空白的系列名永远走 miss 分支。
    public void Lookup_TrimsSourceBeforeMatching()
    {
        Assert.True(SeriesTranslationStore.Lookup("A=甲", " A ", out var target));
        Assert.Equal("甲", target);
        Assert.False(SeriesTranslationStore.Lookup("A=甲", "   ", out _));
    }
}
