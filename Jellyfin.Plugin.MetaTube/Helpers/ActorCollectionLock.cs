namespace Jellyfin.Plugin.MetaTube.Helpers;

/// <summary>
/// 演员合集写入的进程级互斥：每日「全删重建」与单片增量同步共用这把锁。
/// 重建期间增量请求排队等待，避免增量拿着已删除的合集 ID 操作或与重建并发建出重复合集。
/// </summary>
public static class ActorCollectionLock
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public static async Task<IDisposable> AcquireAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new Releaser();
    }

    private sealed class Releaser : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Gate.Release();
        }
    }
}
