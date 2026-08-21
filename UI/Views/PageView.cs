using Avalonia.Controls;
using OppoPodsManager.Assets.UserSettings;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Subsystems.Desktop;
using OppoPodsManager.Control.Subsystems.Logging;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 所有页面视图的基类。MainWindow 外壳在构造后通过 <see cref="Attach"/> 注入共享服务，
/// 并通过 <see cref="ApplySnapshot"/> 把控制层快照路由到对应页面。
/// 视图自己持有 x:Name 控件、事件处理与控件更新逻辑，外壳仅作壳与路由。
/// </summary>
public abstract class PageView : UserControl
{
    public IViewHost? Host { get; set; }

    protected ControlManager? ControlManager;
    protected SettingsStore UiSettings = null!;
    protected ApplicationLog? Log;
    protected CommandDispatcher? CommandDispatcher;
    protected FrontendState? FrontendState;
    protected DesktopLinkService? DesktopLinks;

    public virtual void Attach(
        ControlManager? controlManager,
        SettingsStore uiSettings,
        ApplicationLog? log,
        CommandDispatcher? commandDispatcher,
        FrontendState? frontendState,
        DesktopLinkService? desktopLinks)
    {
        ControlManager = controlManager;
        UiSettings = uiSettings;
        Log = log;
        CommandDispatcher = commandDispatcher;
        FrontendState = frontendState;
        DesktopLinks = desktopLinks;
    }

    /// <summary>
    /// 将控制层发布的不可变快照转换到本页视觉状态。无状态可更新的页面留空实现。
    /// </summary>
    public abstract void ApplySnapshot(BusinessSnapshot snapshot);
}
