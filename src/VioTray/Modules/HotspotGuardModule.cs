using System.Net.NetworkInformation;
using System.Windows.Forms;
using VioTray.Core;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace VioTray.Modules;

/// <summary>
/// 热点守护：定期检查移动热点是否还在工作，掉了就重新拉起来。
/// 判据是 TetheringOperationalState + Wi-Fi Direct 虚拟适配器是否在线，
/// 不能只看 WLAN 物理网卡的连接状态——开热点时它本来就可能显示未连接。
/// </summary>
public sealed class HotspotGuardModule : ITrayModule
{
    private sealed record ProbeResult(bool Healthy, string Text);

    private HotspotOptions _options = new();
    private ModuleContext? _context;
    private volatile string _status = "等待首次检测";
    private volatile bool _paused;
    private int _consecutiveBad;
    private int _failStreak;
    private bool _firstTick = true;
    private bool _loggedFirstProbe;
    private DateTime _nextAttemptUtc = DateTime.MinValue;

    public string Id => "hotspot";

    public string Title => "WiFi 热点";

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(_options.CheckIntervalSeconds, 2, 3600));

    public string Status => _status;

    public void Attach(ModuleContext context)
    {
        _context = context;
        _options = context.Config.Hotspot;
    }

    public async Task TickAsync(CancellationToken ct)
    {
        if (_paused)
            return;

        if (DateTime.UtcNow < _nextAttemptUtc)
            return;

        var probe = await Task.Run(Probe, ct).ConfigureAwait(false);
        _status = probe.Text;
        _context?.SetStatus(Id, probe.Text);

        if (!_loggedFirstProbe)
        {
            _loggedFirstProbe = true;
            Log.Info($"热点首轮检测：{probe.Text}");
        }

        if (probe.Healthy)
        {
            _consecutiveBad = 0;
            _failStreak = 0;
            _firstTick = false;
            return;
        }

        // 启动后第一次就发现是关的，且配置允许，则直接拉起，不等待抖动抑制
        var immediate = _firstTick && _options.StartOnLaunch;
        _firstTick = false;

        _consecutiveBad++;
        if (!immediate && _consecutiveBad < Math.Max(1, _options.RestartAfterFailures))
            return;

        _consecutiveBad = 0;
        Log.Warn($"热点异常：{probe.Text}，开始重启");

        var ok = await RestartAsync(ct).ConfigureAwait(false);
        if (ok)
        {
            _failStreak = 0;
            _nextAttemptUtc = DateTime.MinValue;
            _status = "已恢复";
            _context?.SetStatus(Id, "已恢复");
            Log.Info("热点重启成功");
        }
        else
        {
            _failStreak++;
            var backoff = Math.Min(
                Math.Max(5, _options.MaxBackoffSeconds),
                5 * (int)Math.Pow(3, Math.Min(_failStreak - 1, 4)));
            _nextAttemptUtc = DateTime.UtcNow.AddSeconds(backoff);
            _status = $"恢复失败（{backoff}s 后重试）";
            _context?.SetStatus(Id, _status);
            Log.Warn($"热点重启失败，第 {_failStreak} 次，{backoff}s 后重试");
        }
    }

    private ProbeResult Probe()
    {
        try
        {
            var profile = FindShareProfile();
            if (profile is null)
                return new ProbeResult(false, "找不到共享源网络");

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);
            var state = manager.TetheringOperationalState;

            if (state != TetheringOperationalState.On)
                return new ProbeResult(false, $"热点未开启（{state}）");

            if (!HasWifiDirectAdapter())
                return new ProbeResult(false, "热点虚拟网卡不在线");

            var clients = manager.ClientCount;
            return new ProbeResult(true, clients > 0 ? $"运行中 · {clients} 台设备" : "运行中 · 无设备");
        }
        catch (Exception ex)
        {
            Log.Error("热点状态检测异常", ex);
            return new ProbeResult(false, $"检测异常：{ex.GetType().Name}");
        }
    }

    private async Task<bool> RestartAsync(CancellationToken ct)
    {
        try
        {
            // 网卡重新枚举后旧的 profile 对象会失效，每次都必须重新取
            var profile = FindShareProfile();
            if (profile is null)
            {
                Log.Warn("重启热点失败：没有可用的共享源网络");
                return false;
            }

            var manager = NetworkOperatorTetheringManager.CreateFromConnectionProfile(profile);

            if (manager.TetheringOperationalState == TetheringOperationalState.On)
            {
                var stop = await manager.StopTetheringAsync();
                if (stop.Status != TetheringOperationStatus.Success)
                    Log.Warn($"停止热点返回 {stop.Status}");
            }

            await Task.Delay(Math.Max(0, _options.RestartDelayMs), ct).ConfigureAwait(false);

            var start = await manager.StartTetheringAsync();
            if (start.Status != TetheringOperationStatus.Success)
            {
                Log.Warn($"启动热点返回 {start.Status} {start.AdditionalErrorMessage}");
                return false;
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            Log.Error("重启热点异常", ex);
            return false;
        }
    }

    private ConnectionProfile? FindShareProfile()
    {
        var wanted = _options.ShareSourceAdapter;

        if (!string.IsNullOrWhiteSpace(wanted))
        {
            foreach (var profile in NetworkInformation.GetConnectionProfiles())
            {
                if (!string.Equals(profile.ProfileName, wanted, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (profile.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.None)
                    continue;

                return profile;
            }
        }

        return NetworkInformation.GetInternetConnectionProfile();
    }

    private static bool HasWifiDirectAdapter()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces().Any(n =>
                n.OperationalStatus == OperationalStatus.Up &&
                n.Description.Contains("Wi-Fi Direct", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public IEnumerable<ToolStripMenuItem> BuildMenuItems()
    {
        var pause = new ToolStripMenuItem("暂停热点守护")
        {
            Checked = _paused,
            CheckOnClick = false,
        };
        pause.Click += (_, _) =>
        {
            _paused = !_paused;
            _status = _paused ? "已暂停" : "已恢复守护";
            _context?.SetStatus(Id, _status);
            _context?.RefreshMenu();
            Log.Info(_paused ? "用户暂停热点守护" : "用户恢复热点守护");
        };

        var restart = new ToolStripMenuItem("立即重启热点");
        restart.Click += async (_, _) =>
        {
            try
            {
                _context?.Notify("VioTray", "正在重启热点…");
                var ok = await RestartAsync(CancellationToken.None);
                _context?.Notify("VioTray", ok ? "热点已重启" : "热点重启失败，详情见日志");
            }
            catch (Exception ex)
            {
                Log.Error("手动重启热点失败", ex);
            }
        };

        yield return pause;
        yield return restart;
    }
}
