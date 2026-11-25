using System.Windows.Forms;

namespace VioTray.Core;

/// <summary>
/// 一个功能模块。加到列表里就能被托盘宿主调度，不需要改宿主代码。
/// </summary>
public interface ITrayModule
{
    /// <summary>配置里的唯一标识。</summary>
    string Id { get; }

    /// <summary>菜单和日志里显示的名字。</summary>
    string Title { get; }

    /// <summary>轮询周期，每个模块可以不同。</summary>
    TimeSpan Interval { get; }

    /// <summary>当前状态的一句话描述，显示在托盘菜单顶部。</summary>
    string Status { get; }

    /// <summary>接入宿主时调用一次。</summary>
    void Attach(ModuleContext context);

    /// <summary>按 Interval 周期调用，跑在线程池里，不会阻塞 UI。</summary>
    Task TickAsync(CancellationToken ct);

    /// <summary>模块自己的右键菜单项，可为空。</summary>
    IEnumerable<ToolStripMenuItem> BuildMenuItems();
}

/// <summary>
/// 宿主提供给模块的能力。模块之间不互相引用，只依赖这里。
/// </summary>
public sealed class ModuleContext
{
    public required AppConfig Config { get; init; }

    /// <summary>弹托盘通知。</summary>
    public required Action<string, string> Notify { get; init; }

    /// <summary>请求宿主重建右键菜单（模块状态变化时调用）。</summary>
    public required Action RefreshMenu { get; init; }

    /// <summary>更新模块状态文案。</summary>
    public required Action<string, string> SetStatus { get; init; }
}
