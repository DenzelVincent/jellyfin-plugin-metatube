using Jellyfin.Plugin.MetaTube.Helpers;
using Xunit;

namespace Jellyfin.Plugin.MetaTube.Tests;

/// <summary>
/// 全量重建与单片增量共用一把锁：第二个持有者必须等第一个释放。
/// </summary>
public class ActorCollectionLockTests
{
    [Fact]
    public async Task AcquireAsync_SerializesConcurrentHolders()
    {
        var first = await ActorCollectionLock.AcquireAsync(CancellationToken.None);
        var second = ActorCollectionLock.AcquireAsync(CancellationToken.None);

        await Task.Delay(50);
        Assert.False(second.IsCompleted);

        first.Dispose();

        using var acquired = await second.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.NotNull(acquired);
    }
}
