namespace VioTray.Core;

/// <summary>
/// 单个 Timer 驱动所有模块。各模块按自己的 Interval 到期触发，
/// 在后台线程执行，任何模块抛异常都不影响其他模块。
/// </summary>
internal sealed class Scheduler : IDisposable
{
    private sealed class Entry
    {
        public required ITrayModule Module { get; init; }
        public DateTime NextDueUtc { get; set; }
        public int Running; // 0/1，用 Interlocked 保证不重入
    }

    private readonly List<Entry> _entries = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly System.Threading.Timer _timer;
    private bool _disposed;

    public Scheduler()
    {
        _timer = new System.Threading.Timer(OnTick, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Add(ITrayModule module)
    {
        _entries.Add(new Entry { Module = module, NextDueUtc = DateTime.UtcNow });
    }

    public void Start()
    {
        _timer.Change(1000, 1000);
    }

    private void OnTick(object? state)
    {
        if (_disposed)
            return;

        var now = DateTime.UtcNow;

        foreach (var entry in _entries)
        {
            if (now < entry.NextDueUtc)
                continue;

            if (Interlocked.CompareExchange(ref entry.Running, 1, 0) != 0)
                continue; // 上一轮还没跑完

            entry.NextDueUtc = now.Add(entry.Module.Interval);
            var module = entry.Module;
            var token = _cts.Token;

            _ = Task.Run(async () =>
            {
                try
                {
                    await module.TickAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 正常退出
                }
                catch (Exception ex)
                {
                    Log.Error($"{module.Id} 执行失败", ex);
                }
                finally
                {
                    Interlocked.Exchange(ref entry.Running, 0);
                }
            }, CancellationToken.None);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Change(Timeout.Infinite, Timeout.Infinite);
        _timer.Dispose();
        _cts.Cancel();
        _cts.Dispose();
    }
}
