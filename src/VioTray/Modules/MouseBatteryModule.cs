using System.Globalization;
using System.Windows.Forms;
using VioTray.Core;

namespace VioTray.Modules;

/// <summary>
/// 鼠标电量：读 VXE / ATK 系接收器的厂商私有 HID 报告。
/// 协议已在本机实测（tools/probe_hid.py）：
///   请求 17 字节 [0x08, 0x04, 0..., 校验]，校验 = (0x55 - sum(buf[0:16])) &amp; 0xFF
///   响应 [6]=电量% [7]=充电 [8:10]=毫伏（大端）
/// 只能打开 UsagePage=0xFF02 / Usage=0x02 的那个集合，其他集合全部无响应。
///
/// 提醒策略：一直按周期检测，但只在配置的 RemindAt（默认 17:50）之后才可能提醒，
/// 且需要判断「剩余续航撑不到明天上班」。这样不会在下午两三点就打扰。
/// </summary>
public sealed class MouseBatteryModule : ITrayModule
{
    private sealed record Reading(int Percent, bool Charging, int Millivolts);

    private const byte ReportId = 0x08;
    private const byte CmdPower = 0x04;
    private const byte ChecksumMagic = 0x55;
    private const ushort TargetUsagePage = 0xFF02;
    private const ushort TargetUsage = 0x02;

    private MouseOptions _options = new();
    private ModuleContext? _context;
    private volatile string _status = "等待首次检测";
    private bool _loggedFirstRead;
    private DateTime _lastRemindDay = DateTime.MinValue;
    private DateTime _lastCriticalDay = DateTime.MinValue;

    public string Id => "mouse";

    public string Title => "鼠标电量";

    public TimeSpan Interval => TimeSpan.FromSeconds(Math.Clamp(_options.CheckIntervalSeconds, 20, 3600));

    public string Status => _status;

    public void Attach(ModuleContext context)
    {
        _context = context;
        _options = context.Config.Mouse;
    }

    public async Task TickAsync(CancellationToken ct)
    {
        var reading = await Task.Run(ReadBattery, ct).ConfigureAwait(false);

        if (reading is null)
        {
            _status = "未连接";
            _context?.SetStatus(Id, _status);
            return;
        }

        _status = Format(reading);
        _context?.SetStatus(Id, _status);

        if (!_loggedFirstRead)
        {
            _loggedFirstRead = true;
            Log.Info($"鼠标首轮检测：{_status}");
        }

        MaybeNotify(reading);
    }

    private string Format(Reading reading)
    {
        var parts = new List<string> { $"{reading.Percent}%" };

        if (reading.Charging)
            parts.Add("充电中");
        else
            parts.Add(NeedsCharge(reading) ? "撑不到明天" : "够用");

        parts.Add($"{reading.Millivolts} mV");
        return string.Join(" · ", parts);
    }

    /// <summary>按剩余电量估算能不能撑到明天上班。</summary>
    private bool NeedsCharge(Reading reading)
    {
        if (_options.HoursPerPercent <= 0)
            return false;

        var hoursLeft = reading.Percent * _options.HoursPerPercent;
        return hoursLeft < _options.HoursToCover;
    }

    private void MaybeNotify(Reading reading)
    {
        if (reading.Charging)
            return;

        var now = DateTime.Now;

        // 紧急情况：电量已经低到随时断连，先喊一声，不看时间
        if (_options.CriticalPercent > 0 && reading.Percent <= _options.CriticalPercent)
        {
            if (_lastCriticalDay == now.Date)
                return;

            _lastCriticalDay = now.Date;
            var urgent = $"只剩 {reading.Percent}%，随时可能断连。现在就插上吧。";
            _context?.Notify("鼠标要没电了", urgent);
            Log.Info($"紧急提醒：{urgent}");
            return;
        }

        if (!TryParseTime(_options.RemindAt, out var remindAt))
            return;

        // 还不到点，继续默默检测，不打扰
        if (now.TimeOfDay < remindAt)
            return;

        // 今天已经提醒过
        if (_lastRemindDay == now.Date)
            return;

        // 错过太久就不补了，避免晚上突然弹窗
        var grace = TimeSpan.FromMinutes(Math.Max(0, _options.RemindGraceMinutes));
        if (now.TimeOfDay > remindAt + grace)
            return;

        // 能撑到明天就不用充
        if (!NeedsCharge(reading))
            return;

        _lastRemindDay = now.Date;

        var hoursLeft = reading.Percent * _options.HoursPerPercent;
        var hoursText = hoursLeft >= 1 ? $"{hoursLeft:F0} 小时" : "不到 1 小时";
        var message = $"剩余 {reading.Percent}%，估计还能用 {hoursText}，" +
                      $"撑不到明天上班。下班前插上充电吧。";

        _context?.Notify("该给鼠标充电了", message);
        Log.Info($"充电提醒：{message}");
    }

    private static bool TryParseTime(string text, out TimeSpan value)
        => TimeSpan.TryParseExact(text, @"hh\:mm", CultureInfo.InvariantCulture, out value);

    private Reading? ReadBattery()
    {
        try
        {
            var collections = HidNative.Enumerate(_options.VendorId);
            var target = collections.FirstOrDefault(c =>
                c.UsagePage == TargetUsagePage && c.Usage == TargetUsage);

            if (target is null)
                return null;

            using var device = HidNative.HidDevice.Open(target.Path);
            if (device is null)
                return null;

            if (!device.SetOutputReport(BuildRequest(CmdPower)))
                return null;

            var response = device.ReadReport(Math.Max(target.InputReportLength, 17), 500);
            if (response is null || response.Length < 10)
                return null;

            if (response[0] != ReportId || response[1] != CmdPower)
                return null;

            int percent = response[6];
            if (percent is < 0 or > 100)
                return null;

            int millivolts = (response[8] << 8) | response[9];
            return new Reading(percent, response[7] == 1, millivolts);
        }
        catch (Exception ex)
        {
            Log.Error("读取鼠标电量失败", ex);
            return null;
        }
    }

    private static byte[] BuildRequest(byte command)
    {
        var buffer = new byte[17];
        buffer[0] = ReportId;
        buffer[1] = command;

        var sum = 0;
        for (var i = 0; i < 16; i++)
            sum += buffer[i];

        buffer[16] = (byte)((ChecksumMagic - sum) & 0xFF);
        return buffer;
    }

    public IEnumerable<ToolStripMenuItem> BuildMenuItems()
    {
        var refresh = new ToolStripMenuItem("立即检测电量");
        refresh.Click += async (_, _) =>
        {
            try
            {
                var reading = await Task.Run(ReadBattery).ConfigureAwait(true);
                if (reading is null)
                {
                    _context?.Notify("鼠标电量", "没读到数据，鼠标可能休眠或接收器不在");
                    return;
                }

                _status = Format(reading);
                _context?.SetStatus(Id, _status);
                _context?.Notify("鼠标电量", _status);
            }
            catch (Exception ex)
            {
                Log.Error("手动检测电量失败", ex);
            }
        };

        var testNotify = new ToolStripMenuItem("测试提醒");
        testNotify.Click += (_, _) =>
            _context?.Notify("该给鼠标充电了", "这是一条测试提醒，看到说明通知正常。");

        yield return refresh;
        yield return testNotify;
    }
}
