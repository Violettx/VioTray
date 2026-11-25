using System.Windows.Forms;
using Microsoft.Win32;

namespace VioTray.Core;

/// <summary>
/// 开机启动：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run，
/// 只对当前用户生效，不需要管理员权限。任务管理器的「启动」页能看到同一项。
/// </summary>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "VioTray";

    /// <summary>当前进程的 exe 路径（单文件发布下也准确）。</summary>
    public static string ExecutablePath =>
        Environment.ProcessPath ?? Application.ExecutablePath;

    /// <summary>注册表里的路径和当前 exe 一致才算已启用（程序被移动过会返回 false）。</summary>
    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
            if (key?.GetValue(ValueName) is not string raw || raw.Length == 0)
                return false;

            var stored = raw.Trim().Trim('"');
            return string.Equals(stored, ExecutablePath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log.Error("读取开机启动状态失败", ex);
            return false;
        }
    }

    public static bool Enable()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
            if (key is null)
                return false;

            key.SetValue(ValueName, $"\"{ExecutablePath}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("设置开机启动失败", ex);
            return false;
        }
    }

    public static bool Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
            key?.DeleteValue(ValueName, false);
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("取消开机启动失败", ex);
            return false;
        }
    }

    /// <summary>切换并返回切换后的状态，失败时返回 null。</summary>
    public static bool? Toggle()
    {
        var enable = !IsEnabled();
        var ok = enable ? Enable() : Disable();
        return ok ? enable : null;
    }
}
