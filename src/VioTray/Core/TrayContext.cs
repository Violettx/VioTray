using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using VioTray.Modules;

namespace VioTray.Core;

internal sealed class TrayContext : ApplicationContext
{
    private readonly AppConfig _config;
    private readonly NotifyIcon _icon;
    private readonly Scheduler _scheduler = new();
    private readonly List<ITrayModule> _modules = new();
    private readonly Dictionary<string, string> _statuses = new();
    private readonly Control _marshal = new();

    public TrayContext(AppConfig config)
    {
        _config = config;
        _ = _marshal.Handle; // 强制创建句柄，供跨线程回调使用

        _icon = new NotifyIcon
        {
            Icon = AppIcon.Create(),
            Text = "VioTray",
            Visible = true,
        };
        _icon.ContextMenuStrip = new ContextMenuStrip();
        _icon.ContextMenuStrip.Opening += (_, _) => RebuildMenu();
        _icon.DoubleClick += (_, _) => Notify("VioTray", StatusSummary());

        RegisterModules();

        var ctx = new ModuleContext
        {
            Config = _config,
            Notify = Notify,
            RefreshMenu = () => Post(RebuildMenu),
            SetStatus = (id, text) =>
            {
                _statuses[id] = text;
                Post(UpdateTooltip);
            },
        };

        foreach (var module in _modules)
        {
            try
            {
                module.Attach(ctx);
                _scheduler.Add(module);
            }
            catch (Exception ex)
            {
                Log.Error($"模块 {module.Id} 初始化失败", ex);
            }
        }

        RebuildMenu();
        _scheduler.Start();

        Log.Info($"托盘启动完成，模块：{string.Join(", ", _modules.Select(m => m.Id))}");
        Log.Info($"开机启动：{(AutoStart.IsEnabled() ? "已启用" : "未启用")}");
        Notify("VioTray", "已启动，正在守护");
    }

    private void RegisterModules()
    {
        if (_config.Hotspot.Enabled)
            _modules.Add(new HotspotGuardModule());

        if (_config.Mouse.Enabled)
            _modules.Add(new MouseBatteryModule());
    }

    private void Post(Action action)
    {
        if (_marshal.IsDisposed)
            return;

        try
        {
            if (_marshal.InvokeRequired)
                _marshal.BeginInvoke(action);
            else
                action();
        }
        catch (Exception ex)
        {
            Log.Error("UI 回调失败", ex);
        }
    }

    private void Notify(string title, string text)
    {
        Post(() =>
        {
            try
            {
                _icon.ShowBalloonTip(5000, title, text, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log.Error("气泡通知失败", ex);
            }
        });
    }

    private string StatusSummary()
    {
        if (_modules.Count == 0)
            return "没有启用的模块";

        return string.Join(Environment.NewLine, _modules.Select(m =>
            $"{m.Title}：{(_statuses.TryGetValue(m.Id, out var s) ? s : "等待首次检测")}"));
    }

    private void UpdateTooltip()
    {
        try
        {
            var parts = _modules.Select(m =>
                _statuses.TryGetValue(m.Id, out var s) ? $"{m.Title} {s}" : m.Title);
            var text = "VioTray · " + string.Join(" · ", parts);
            if (text.Length > 63)
                text = text[..63];
            _icon.Text = text;
        }
        catch
        {
            // tooltip 长度问题不值得打断
        }
    }

    private void RebuildMenu()
    {
        var menu = _icon.ContextMenuStrip;
        if (menu is null)
            return;

        menu.Items.Clear();

        menu.Items.Add(new ToolStripMenuItem("VioTray") { Enabled = false });
        menu.Items.Add(new ToolStripSeparator());

        if (_modules.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("没有启用的模块") { Enabled = false });
        }
        else
        {
            foreach (var module in _modules)
            {
                var status = _statuses.TryGetValue(module.Id, out var s) ? s : "等待首次检测";
                menu.Items.Add(new ToolStripMenuItem($"{module.Title}：{status}") { Enabled = false });
            }
        }

        var moduleItems = _modules.SelectMany(m => m.BuildMenuItems()).ToList();
        if (moduleItems.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var item in moduleItems)
                menu.Items.Add(item);
        }

        menu.Items.Add(new ToolStripSeparator());

        var autoStart = new ToolStripMenuItem("开机启动")
        {
            Checked = AutoStart.IsEnabled(),
            CheckOnClick = false,
        };
        autoStart.Click += (_, _) => ToggleAutoStart();
        menu.Items.Add(autoStart);

        menu.Items.Add(new ToolStripMenuItem("打开配置", null, (_, _) => OpenPath(Paths.ConfigFile)));
        menu.Items.Add(new ToolStripMenuItem("打开日志目录", null, (_, _) => OpenPath(Paths.LogDir)));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("退出", null, (_, _) => ExitApp()));
    }

    private void ToggleAutoStart()
    {
        var result = AutoStart.Toggle();

        if (result is null)
        {
            Notify("VioTray", "修改开机启动失败，详情见日志");
            return;
        }

        var enabled = result.Value;
        Log.Info(enabled ? "已开启开机启动" : "已关闭开机启动");
        Notify("VioTray", enabled ? "已设置为开机启动" : "已取消开机启动");

        // 菜单此时可能还没关，延后重建
        Post(RebuildMenu);
    }

    private static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"打开 {path} 失败", ex);
        }
    }

    private void ExitApp()
    {
        Log.Info("用户请求退出");
        _icon.Visible = false;
        _scheduler.Dispose();
        _marshal.Dispose();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icon.Dispose();
            _scheduler.Dispose();
        }

        base.Dispose(disposing);
    }
}

internal static class AppIcon
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static Icon Create()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            using var bg = new SolidBrush(Color.FromArgb(29, 158, 117));
            g.FillEllipse(bg, 1, 1, 30, 30);

            using var font = new Font("Segoe UI", 17, FontStyle.Bold, GraphicsUnit.Pixel);
            using var fg = new SolidBrush(Color.White);
            using var fmt = new StringFormat
            {
                Alignment = StringAlignment.Center,
                LineAlignment = StringAlignment.Center,
            };
            g.DrawString("V", font, fg, new RectangleF(0, 0, 32, 33), fmt);
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }
}
