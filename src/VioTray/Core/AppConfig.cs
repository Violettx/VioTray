using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VioTray.Core;

public sealed class AppConfig
{
    /// <summary>配置结构版本。旧配置读进来后会自动补全新字段并回写。</summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; }

    public HotspotOptions Hotspot { get; set; } = new();

    public MouseOptions Mouse { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static AppConfig Load(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);
                if (cfg is not null)
                {
                    if (cfg.Version < CurrentVersion)
                    {
                        cfg.Version = CurrentVersion;
                        try
                        {
                            cfg.Save(path);
                            Log.Info($"配置已升级到 v{CurrentVersion}");
                        }
                        catch (Exception ex)
                        {
                            Log.Error("回写升级后的配置失败", ex);
                        }
                    }

                    return cfg;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("读取配置失败，改用默认值", ex);
        }

        var fallback = new AppConfig { Version = CurrentVersion };
        try
        {
            fallback.Save(path);
        }
        catch (Exception ex)
        {
            Log.Error("写入默认配置失败", ex);
        }

        return fallback;
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions);
        File.WriteAllText(path, json);
    }
}

public sealed class HotspotOptions
{
    /// <summary>是否启用热点守护。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>共享来源的适配器名，比如「以太网」。留空则用系统默认上网连接。</summary>
    public string ShareSourceAdapter { get; set; } = "以太网";

    /// <summary>检测周期（秒）。</summary>
    public int CheckIntervalSeconds { get; set; } = 5;

    /// <summary>连续几次异常才执行重启，用来抑制抖动。</summary>
    public int RestartAfterFailures { get; set; } = 2;

    /// <summary>Stop 之后等多久再 Start（毫秒）。</summary>
    public int RestartDelayMs { get; set; } = 1500;

    /// <summary>连续失败时的最大退避（秒）。</summary>
    public int MaxBackoffSeconds { get; set; } = 60;

    /// <summary>启动时如果发现热点是关的，是否主动打开。</summary>
    public bool StartOnLaunch { get; set; } = true;
}

public sealed class MouseOptions
{
    /// <summary>是否启用鼠标电量监测。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>轮询周期（秒）。只影响检测精度，不影响提醒时间。</summary>
    public int CheckIntervalSeconds { get; set; } = 60;

    /// <summary>提醒时刻，HH:mm。到点才提醒，之前只默默检测不打扰。</summary>
    public string RemindAt { get; set; } = "17:50";

    /// <summary>错过提醒时刻后，还能补提醒多久（分钟）。超出就不补了。</summary>
    public int RemindGraceMinutes { get; set; } = 40;

    /// <summary>需要撑到明天上班的小时数（17:50 到次日 09:00 约 15.2）。</summary>
    public double HoursToCover { get; set; } = 16.0;

    /// <summary>
    /// 每 1% 电量大约能用多少小时，用来估算剩余续航。
    /// 校准方法：记下满电到没电用了几天，除以 100。默认 0.7 偏保守。
    /// </summary>
    public double HoursPerPercent { get; set; } = 0.7;

    /// <summary>低于此电量立刻提醒，不看时间（怕断连）。设 0 关闭。</summary>
    public int CriticalPercent { get; set; } = 5;

    /// <summary>接收器厂商 ID，VXE / ATK 系为 0x3554。</summary>
    public ushort VendorId { get; set; } = 0x3554;
}
