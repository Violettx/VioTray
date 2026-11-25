# VioTray

一个常驻 Windows 托盘的轻量工具，用可插拔模块承载多个小功能。

目前包含两个模块：

- **热点守护** — 定时检查 Windows 移动热点是否还在工作，掉了自动重开
- **鼠标电量** — 读取 VXE / ATK 系无线鼠标的剩余电量，判断能否撑到第二天，不够就在下班前提醒充电

## 背景

USB 二合一 WiFi + 蓝牙网卡在过热或供电波动时会掉线，重连后 Windows 不会自动恢复移动热点。VioTray 负责盯住这件事。

鼠标电量部分是因为 Windows 本身不提供 2.4G 接收器鼠标的电量，只能读厂商私有 HID 协议。

## 系统要求

- Windows 10 1809 或更高（需要 WinRT 的 `NetworkOperatorTetheringManager`）
- [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)

## 安装

1. 从 [Releases](../../releases) 下载 `VioTray.exe`
2. 放到任意目录，双击运行
3. 托盘右键可勾选「开机启动」

配置与日志位于 `%LOCALAPPDATA%\VioTray\`。

## 配置

编辑 `%LOCALAPPDATA%\VioTray\config.json`，改完重启程序生效。

```json
{
  "Version": 2,
  "Hotspot": {
    "Enabled": true,
    "ShareSourceAdapter": "以太网",
    "CheckIntervalSeconds": 5,
    "RestartAfterFailures": 2,
    "RestartDelayMs": 1500,
    "MaxBackoffSeconds": 60,
    "StartOnLaunch": true
  },
  "Mouse": {
    "Enabled": true,
    "CheckIntervalSeconds": 60,
    "RemindAt": "17:50",
    "RemindGraceMinutes": 40,
    "HoursToCover": 16.0,
    "HoursPerPercent": 0.7,
    "CriticalPercent": 5,
    "VendorId": 13652
  }
}
```

`ShareSourceAdapter` 是热点的共享来源适配器名，比如「以太网」；留空则使用系统默认上网连接。

### 鼠标提醒逻辑

检测每 60 秒执行一次，但提醒只在 `RemindAt` 之后才可能触发，下午不会提前打扰：

1. 充电中 → 不提醒
2. 电量 ≤ `CriticalPercent` → 立即提醒（怕随时断连，不看时间）
3. 未到 `RemindAt` → 只更新托盘状态
4. 今天已提醒过 → 跳过
5. 超过 `RemindAt + RemindGraceMinutes` → 不补提醒，避免晚上弹窗
6. `剩余电量 × HoursPerPercent ≥ HoursToCover` → 够用，不提醒

`HoursPerPercent` 需要按实际续航校准：满电用到 0 花了几天，`天数 × 24 ÷ 100` 就是准确值。

## 构建

需要 .NET 10 SDK。

```powershell
.\build.ps1
```

产物：`src\VioTray\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\VioTray.exe`

## 部署

```powershell
.\install.ps1     # 复制到 %LOCALAPPDATA%\VioTray，注册自启并启动
.\uninstall.ps1   # 停止进程，移除自启，删除目录
```

脚本为 UTF-8 with BOM，PowerShell 5.1 才能正确读取中文。

## 扩展新模块

实现 `ITrayModule`，然后在 `TrayContext.RegisterModules()` 里加一行即可，托盘和调度代码无需改动。

```csharp
public interface ITrayModule
{
    string Id { get; }
    string Title { get; }
    TimeSpan Interval { get; }
    string Status { get; }
    void Attach(ModuleContext context);
    Task TickAsync(CancellationToken ct);
    IEnumerable<ToolStripMenuItem> BuildMenuItems();
}
```

调度器用单个 Timer 驱动所有模块，模块在后台线程执行，某个模块抛异常不影响其他模块。

## 技术说明

- .NET 10 + WinForms，单文件发布
- 热点控制走 WinRT `NetworkOperatorTetheringManager`
- 鼠标电量走 `hid.dll` / `setupapi.dll` P/Invoke

### VXE / ATK 鼠标 HID 协议

```
请求 17 字节：
  [0]     0x08        report id
  [1]     0x04        CMD_POWER
  [2..15] 0x00
  [16]    校验和 = (0x55 - sum(buf[0:16])) & 0xFF

响应 17 字节：
  [6]     电量百分比 0-100
  [7]     充电状态 0/1
  [8:10]  电压毫伏（大端）
```

只能打开 `UsagePage = 0xFF02`、`Usage = 0x02` 的 HID 集合，用 `HidD_SetOutputReport` 发送。同一设备上其他 vendor 集合（`0xFF03` / `0xFF04` / `0xFF05`）全部无响应。

协议参考：[python-pulsar-mouse-tool](https://github.com/andrewrabert/python-pulsar-mouse-tool)、Linux 内核 `drivers/hid/hid-pulsar.c`。

## 已知限制

- 鼠标电量只验证了 2.4G 接收器，有线 / 蓝牙连接未测试
- 4K 接收器的报告长度未验证
- 托盘常驻内存约 62 MB，主要来自 WinRT 投影程序集 `Microsoft.Windows.SDK.NET.dll`

## License

MIT
