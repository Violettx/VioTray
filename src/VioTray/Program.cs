using System.Windows.Forms;
using VioTray.Core;

namespace VioTray;

internal static class Program
{
    private const string MutexName = @"Local\VioTray.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var isFirstInstance);
        if (!isFirstInstance)
            return;

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Paths.EnsureCreated();
        Log.Cleanup();
        Log.Info("VioTray 启动");

        AppConfig config;
        try
        {
            config = AppConfig.Load(Paths.ConfigFile);
        }
        catch (Exception ex)
        {
            Log.Error("加载配置失败", ex);
            config = new AppConfig();
        }

        try
        {
            using var context = new TrayContext(config);
            Application.Run(context);
        }
        catch (Exception ex)
        {
            Log.Error("主循环异常退出", ex);
            MessageBox.Show(ex.ToString(), "VioTray 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }

        Log.Info("VioTray 退出");
    }
}
