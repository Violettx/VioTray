# VioTray 规划

一个常驻 Windows 托盘的轻量工具，用插件式模块承载多个小功能。

## 一、实测结论（本机环境）

| 项目 | 结果 |
|---|---|
| 系统 | Windows 10 22H2 (19045) |
| .NET 运行时 | 10.0.8（含 WindowsDesktop），**未装 SDK** |
| 其他工具链 | 无 Rust / Go / MSVC |
| USB 网卡 | AIC8800 方案，`VID_A69C&PID_8D81`（MI_00=BT，MI_02=WiFi），iGrentech 驱动 |
| 移动热点 | **API 可用**，当前 On，SSID `<your-ssid>`，共享源=以太网，ClientCount=1 |
| 鼠标电量 | 系统 HID Battery 属性**全部为空**，必须走私有协议 |
| 鼠标 VID | `VID_3554` 下有 F406 / F58E / F58F / FA09 多个历史 PID |

## 二、技术选型

**选择：C# / .NET 10 + WinForms 托盘，框架依赖发布（单 exe ~200-400KB）**

理由：
1. 热点控制是 WinRT API，C# 有官方投影，直通；C++ 需 C++/WinRT，Rust 需 windows-rs 绕层
2. 机器已装 .NET 10 运行时，不必打包运行时
3. 内存 30-40MB，CPU 空闲 ~0%
4. 扩展成本最低：一个 interface 一个类

备选（若追求 <15MB）：
- C# + 纯 Win32 消息循环（不用 WinForms）：10-15MB
- Rust + windows-rs：6-10MB，但需装 VS Build Tools

已排除：Electron、Tauri（WebView2 方案对本需求过重）。

## 三、架构

```
VioTray.exe（单实例，Mutex）
├─ Core
│  ├─ TrayHost      托盘图标 / 右键菜单 / 气泡通知
│  ├─ Scheduler     单 Timer，按模块各自 Interval 派发
│  ├─ Config        JSON 配置，改完重启程序生效
│  └─ Logger        按天滚动，保留 7 天
└─ Modules（可插拔）
   ├─ HotspotGuard  WiFi 热点守护
   ├─ MouseBattery  鼠标电量监测
   └─ ...           以后新增
```

模块接口：

```csharp
public interface ITrayModule
{
    string Id { get; }
    string Title { get; }
    TimeSpan Interval { get; }
    Task TickAsync(CancellationToken ct);
    IReadOnlyList<MenuEntry> BuildMenu();
}
```

约定：
- 模块之间不互相引用，只依赖 Core 暴露的 `IModuleContext`（日志 / 通知 / 配置）
- 单个模块异常被 Scheduler 捕获并隔离，不影响其他模块
- 加功能 = 新增一个类 + 配置 enable 一行

## 四、HotspotGuard

### 检测（每 5 秒）
- `TetheringOperationalState`（On / Off / Unknown）
- WLAN 适配器 `OperationalStatus`
- `ClientCount`

### 判定
- 状态 != On → 异常
- 状态 == On 但 WLAN 掉线且 ClientCount == 0，连续 3 次 → 判为"假活"，异常

### 恢复
1. 重新 `CreateFromConnectionProfile` 获取 manager（网卡重枚举后旧对象失效）
2. `StopTetheringAsync()` → 等 1.5s → `StartTetheringAsync()`
3. 失败退避：5s → 15s → 60s，连续失败 5 次后弹通知告警

### 坑
- 用户手动关热点不得抢回 → 菜单"守护开关"
- Windows 10 默认"无设备连接 5 分钟后自动关闭热点"，建议系统设置里关掉
- 重启会断开已连设备，必须做连续 N 次异常的抖动抑制
- 网卡掉线瞬间 API 抛异常，需 try/catch 并把异常视为异常状态

## 五、MouseBattery（协议已验证）

### 结论
Windows 不提供 2.4G 接收器鼠标电量（全部 HID Battery 属性为空），必须读厂商私有协议。
**2026-11-25 已在本机实测通过**，探测脚本见 `tools/probe_hid.py`。

### 实测结果

| 项目 | 值 |
|---|---|
| 设备 | `VID_3554` `PID_F58E`，collection 路径含 `mi_01&col05` |
| 定位条件 | **UsagePage = 0xFF02，Usage = 0x02**（唯一可通信的集合） |
| 报告长度 | Input 17 / Output 17 |
| 发送方式 | `HidD_SetOutputReport`（**不是** WriteFile，**不是** Feature Report） |
| 读取方式 | `ReadFile` + Overlapped，超时 400ms |

### 协议

```
请求 17 字节：
  [0]    0x08        report id
  [1]    0x04        CMD_POWER
  [2..15] 0x00
  [16]   校验和 = (0x55 - sum(buf[0:16])) & 0xFF

响应 17 字节：
  [0]    0x08
  [1]    0x04        命令回显
  [6]    电量百分比 0-100
  [7]    充电状态 0/1
  [8:10] 电压毫伏，大端
```

实测样本：`08 04 00 00 00 02 64 00 10 33 00 00 00 00 00 00 A0`
→ 100%、未充电、4147 mV

### 踩坑记录
- 同设备上 `0xFF03` / `0xFF04` / `0xFF05` 及标准 HID 集合**全部无响应**，必须按 usage page 精确匹配，不能靠 VID/PID 猜
- `VID_258A&PID_0143` 是另一个键鼠设备（SinoWealth 方案），与本鼠标无关
- 鼠标休眠时读取会超时，属正常，需静默跳过

### 参考实现
- `andrewrabert/python-pulsar-mouse-tool` —— 协议来源（MIT）
- Linux `drivers/hid/hid-pulsar.c` —— 完整命令字，明确支持 VXE R1 SE+
- `Fan4Metal/mouse_tray` —— Windows 实现，支持列表含 VXE R1 SE+

### 仍未覆盖
- 4K 接收器（`CON_4K`）的报告长度未验证，当前只有 1K 接收器实测
- 有线 / 蓝牙连接方式的上报路径不同，暂不处理

### 提醒策略（v2）

**检测一直按周期进行，但提醒只在 17:50 之后才可能触发。** 下午两三点发现电量低也不会打扰。

判定顺序：

1. 充电中 → 不提醒
2. 电量 ≤ `CriticalPercent`（默认 5%）→ 立即提醒一次，不看时间（怕随时断连）
3. 还没到 `RemindAt`（默认 17:50）→ 只更新托盘状态，不打扰
4. 今天已经提醒过 → 跳过
5. 超过 `RemindAt + RemindGraceMinutes`（默认 40 分钟）→ 不补提醒，避免晚上弹窗
6. 剩余续航 ≥ `HoursToCover`（默认 16 小时）→ 够用到明天，不提醒

续航估算：`剩余电量 × HoursPerPercent`。默认 0.7，即满电约 70 小时。

**校准方法**：从满电用到 0 花了几天，`天数 × 24 ÷ 100` 就是真实的 `HoursPerPercent`。
嫌提醒太早（电量还挺多）就调大，嫌提醒太晚就调小。

相关配置：

```json
"Mouse": {
  "RemindAt": "17:50",           // 提醒时刻
  "RemindGraceMinutes": 40,      // 错过后的补提醒窗口
  "HoursToCover": 16.0,          // 17:50 到次日上班约 15.2 小时
  "HoursPerPercent": 0.7,        // 每 1% 电量能用多少小时
  "CriticalPercent": 5           // 低于此值立即提醒，0 关闭
}
```

改这些不需要重新编译，改完重启程序即可。
- 设备不在线（关机 / 休眠 / 接收器拔出）静默跳过

## 六、硬件层建议（治本）

- 设备管理器 → USB 根集线器 → 电源管理 → 取消"允许计算机关闭此设备以节约电源"
- 电源选项 → USB 设置 → 禁用"USB 选择性挂起"
- 优先插 USB 2.0 口（3.0 对 2.4G 干扰大，AIC8800 在 3.0 口供电波动明显）
- 加强散热（USB 小风扇 / 散热片）

## 七、部署

- 目录：`%LOCALAPPDATA%\VioTray\`
- 自启：`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，不需要管理员
- 单实例：命名 Mutex
- 日志：`%LOCALAPPDATA%\VioTray\logs\`

## 八、里程碑

| 阶段 | 内容 | 预估 |
|---|---|---|
| M1 | 托盘壳 + 配置 + 日志 + 单实例 + 自启 | 半天 |
| M2 | HotspotGuard | 半天 |
| M3 | MouseBattery（含协议校准） | 1 天 |
| M4 | 打磨：图标、通知、安装脚本 | 半天 |

---

## 九、实现状态（2026-11-25）

M1–M4 已完成，代码可编译运行。

### 项目结构

```
src/VioTray/
├─ VioTray.csproj          net10.0-windows10.0.19041.0 + WinForms
├─ app.manifest            仅声明 Win10 兼容性（DPI 走 csproj 属性）
├─ Program.cs              单实例入口
├─ Core/
│  ├─ ITrayModule.cs       模块接口 + ModuleContext
│  ├─ AppConfig.cs         JSON 配置模型
│  ├─ Scheduler.cs         单 Timer，多周期，异常隔离
│  ├─ TrayContext.cs       托盘 + 菜单 + 气泡 + 图标
│  ├─ Log.cs / Paths.cs
└─ Modules/
   ├─ HotspotGuardModule.cs
   ├─ MouseBatteryModule.cs
   └─ HidNative.cs          hid.dll / setupapi.dll P/Invoke
```

### 实测结果

| 项目 | 值 |
|---|---|
| 编译 | 0 警告 0 错误 |
| 鼠标电量 | 100% · 4142 mV（与 Python 探测的 4147 mV 吻合） |
| 热点状态 | 运行中 · 1 台设备 |
| 发布体积 | 单 exe 25.6 MB（框架依赖） |
| 运行内存 | 62 MB 工作集 |
| CPU | 空闲 0% |

### 体积/内存构成

25.6 MB 里约 25 MB 来自 `Microsoft.Windows.SDK.NET.dll`（WinRT 投影），是使用
`NetworkOperatorTetheringManager` 的代价。内存 62 MB 同源。

若要把内存压到 20 MB 以内，需放弃 WinRT 投影，改用 COM ABI 直接调用
`INetworkOperatorTetheringManager`（省约 15–20 MB），或把 UI 层换成纯 Win32
消息循环（再省约 10 MB）。工作量和风险都明显上升，当前判断不值得。

### 已验证

| 测试 | 结果 |
|---|---|
| 鼠标电量读取 | 100% · 4142 mV，与 Python 探测一致 |
| 热点自动恢复 | 手动关闭后 **12 秒**恢复（抖动抑制约 10 秒 + 重启 2.3 秒） |
| 部署 | 已装到 `%LOCALAPPDATA%\VioTray`，开机自启已注册 |

恢复过程日志：

```
10:37:44.351 [WARN] 热点异常：热点未开启（Off），开始重启
10:37:46.680 [INFO] 热点重启成功
```

### 已知问题

- PowerShell 5.1 按 GBK 读取无 BOM 的 .ps1，脚本若含中文会解析失败。
  `install.ps1` / `build.ps1` / `uninstall.ps1` 已保存为 UTF-8 with BOM。
- 由受限进程（如沙箱）启动时读不到 HID 设备，托盘会显示"未连接"。
  正常双击或开机自启不受影响。

### 使用方式

```
build.ps1      编译并发布单文件
install.ps1    安装到 %LOCALAPPDATA%\VioTray，注册开机自启并启动
uninstall.ps1  停止、移除自启、删除安装目录
```

托盘右键菜单：各模块状态、暂停热点守护、立即重启热点、立即检测电量、
测试提醒、**开机启动（勾选）**、打开配置、打开日志目录、退出。

「开机启动」勾选状态实时读注册表（`HKCU\...\Run\VioTray`），点一下即切换，
不需要重启程序。如果程序被移动到别的路径，旧注册表值会被判定为未启用，
重新勾选即可写入新路径。

