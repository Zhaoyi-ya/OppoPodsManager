using OppoPodsManager.Control.Gestures;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 快捷手势独立页：从主页面 HomeView 拆分出来，承载左右耳触控手势配置。
/// UI 复用共享 <see cref="GestureUi"/> 按设备能力动态生成（多品牌兼容），
/// 外壳经 <see cref="ApplySnapshot"/> 路由快照，并经 IViewHost 下发手势变更。
/// </summary>
public partial class GestureView : PageView
{
    // 快照重建时抑制 SelectionChanged 误触发下发。
    private bool _suppressGestureSelection;

    public GestureView()
    {
        InitializeComponent();
    }

    public override void ApplySnapshot(BusinessSnapshot snapshot)
    {
        var manager = ControlManager?.ActiveManager;
        var entries = manager?.GestureEntries;

        // 未连接设备，或当前设备无手势配置入口时：隐藏配置卡片，显示占位提示。
        if (manager is null || !snapshot.IsConnected || entries is null || entries.Count == 0)
        {
            GestureCard.IsVisible = false;
            GestureLeftHost.Children.Clear();
            GestureRightHost.Children.Clear();
            GestureEmpty.IsVisible = true;
            return;
        }

        GestureEmpty.IsVisible = false;
        _suppressGestureSelection = true;
        try
        {
            GestureUi.Rebuild(GestureLeftHost, GestureRightHost, entries, OnGestureSet);
        }
        finally
        {
            _suppressGestureSelection = false;
        }

        GestureCard.IsVisible = true;
    }

    private void OnGestureSet(EarSide ear, TapKind kind, GestureActionKind action)
    {
        if (_suppressGestureSelection)
            return;
        _ = CommandDispatcher?.RunAsync("触控手势",
            m => m.SetTouchGestureAsync(ear, kind, action, CancellationToken.None));
    }
}
