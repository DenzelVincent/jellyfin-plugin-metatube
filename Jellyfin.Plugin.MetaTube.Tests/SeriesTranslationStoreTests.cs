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
}
