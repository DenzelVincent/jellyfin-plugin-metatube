using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 演员合集媒体库范围的纯判据：空范围 = 全部库；解析时忽略无效与重复项。
/// 查询层与增量监听都依赖这两条口径，判错会让升级用户突然少掉一半成员。
/// </summary>
public class ActorCollectionScopeTests
{
    private static readonly Guid LibraryA = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid LibraryB = Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    // 旧配置没有这个字段，空/无效值必须退回「全部库」，不能把演员合集整个关掉。
    public void ParseLibraryIds_TreatsMissingOrInvalidAsAllLibraries()
    {
        Assert.Empty(ActorCollectionScope.ParseLibraryIds(null));
        Assert.Empty(ActorCollectionScope.ParseLibraryIds(Array.Empty<string>()));
        Assert.Empty(ActorCollectionScope.ParseLibraryIds(new[] { "", "   ", "not-a-guid" }));
    }

    [Fact]
    // 配置页传来的库 id 逐个解析；重复项去重并保持首次出现顺序，查询与日志才稳定。
    public void ParseLibraryIds_KeepsValidIdsOnceInFirstSeenOrder()
    {
        var parsed = ActorCollectionScope.ParseLibraryIds(new[]
        {
            LibraryA.ToString(), LibraryB.ToString(), LibraryA.ToString().ToUpperInvariant()
        });

        Assert.Equal(new[] { LibraryA, LibraryB }, parsed);
    }

    [Fact]
    // 空范围表示全部库：升级前的行为不能变。
    public void Includes_EmptyScopeIncludesEveryLibrary()
    {
        Assert.True(ActorCollectionScope.Includes(Array.Empty<Guid>(), new[] { LibraryA }));
        Assert.True(ActorCollectionScope.Includes(null, Array.Empty<Guid>()));
    }

    [Fact]
    // 链接条目可能同时属于多个库：任一所属库命中即视为在范围内。
    public void Includes_TrueWhenAnyOwningLibraryIsSelected()
    {
        Assert.True(ActorCollectionScope.Includes(new[] { LibraryB }, new[] { LibraryA, LibraryB }));
        Assert.False(ActorCollectionScope.Includes(new[] { LibraryB }, new[] { LibraryA }));
        Assert.False(ActorCollectionScope.Includes(new[] { LibraryB }, Array.Empty<Guid>()));
    }
}
