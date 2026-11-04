using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 空合集判据的离线防线：链接还在、条目被删也算空（LinkedChildren 的外键是 NoAction，不会级联清）。
/// </summary>
public class EmptyCollectionCleanerTests
{
    [Fact]
    public void HasLivingMember_TrueWhenAnyMemberStillExists()
    {
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        Assert.True(EmptyCollectionCleaner.HasLivingMember(ids, id => id == ids[1]));
    }

    [Fact]
    public void HasLivingMember_FalseWhenAllMembersAreGone()
    {
        Assert.False(EmptyCollectionCleaner.HasLivingMember(new[] { Guid.NewGuid() }, _ => false));
    }

    [Fact]
    public void HasLivingMember_FalseForEmptyOrNullMemberList()
    {
        Assert.False(EmptyCollectionCleaner.HasLivingMember(Array.Empty<Guid>(), _ => true));
        Assert.False(EmptyCollectionCleaner.HasLivingMember(null, _ => true));
    }
}
