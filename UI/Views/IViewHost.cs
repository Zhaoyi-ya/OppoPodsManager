using System.Threading.Tasks;
using OppoPodsManager.Assets.Localization;

namespace OppoPodsManager.UI.Views;

/// <summary>
/// 视图宿主（MainWindow 外壳）向各页面视图暴露的回调能力：
/// 导航、打开外链、弹出对话框，以及个性化页所需的若干外壳级外观副作用
/// （主题应用、语言切换、窗口背景、卡片透明度、标题、高级渲染/Acrylic）。
/// 视图不直接持有外壳控件，仅通过该接口交互。
/// </summary>
public interface IViewHost
{
    void RequestNavigate(string page);
    void OpenUrl(string url);
    Task ShowCheckResultDialogAsync(string message, string? title = null);
    Task<string?> ShowPromptDialogAsync(string title, string defaultText, string hint);
    Task<bool> ShowConfirmDialogAsync(string title, string message);

    // ---- 个性化页外壳级副作用 ----
    /// <summary>应用主题（窗体外观），并持久化主题选择。</summary>
    void ApplyTheme(int index);
    /// <summary>切换界面语言并刷新所有本地化敏感的控件与状态。</summary>
    void ApplyLanguage(LanguageOption option);
    /// <summary>按当前卡片透明度设置刷新外壳复用画刷（不触发完整状态刷新）。</summary>
    void RefreshCardOpacity();
    /// <summary>选择并应用指定背景（含默认背景）。</summary>
    void SelectBackground(string key);
    /// <summary>打开文件选择器添加一张自定义背景图片。</summary>
    void AddBackgroundImage();
    /// <summary>背景模糊值已变更，触发窗口背景防抖重绘。</summary>
    void ApplyBackgroundBlur();
    /// <summary>应用并持久化 Acrylic 模糊开关（用户手动切换时调用，会弹出提示）。</summary>
    void SetAcrylicBlur(bool on);
    /// <summary>静默应用并持久化 Acrylic 模糊开关（启动期调用，避免每次开 APP 都弹提示）。</summary>
    void SetAcrylicBlurSilent(bool on);
    /// <summary>应用并持久化高级渲染开关。</summary>
    void SetAdvancedRender(bool on);
    /// <summary>设备自定义名已变更，持久化并刷新窗口标题。</summary>
    void SetCustomDeviceName(string? name);
    /// <summary>重建自定义耳机图案行（切语言后调用）。</summary>
    void RebuildEarphoneUi();
    /// <summary>按当前背景历史重建缩略图并应用窗口背景。</summary>
    void RefreshBackground();

    // ---- 设置页外壳级能力 ----
    /// <summary>执行手动检查更新（外壳持有更新协调器与结果对话框）。</summary>
    Task CheckForUpdatesAsync();
    /// <summary>打开反馈对话框并导出日志到桌面。</summary>
    Task OpenFeedbackAsync();
    /// <summary>请求外壳重建侧栏多设备列表并刷新「恢复隐藏设备」按钮。</summary>
    void ResyncMultiDeviceList();

    // ---- 主页视图外壳级能力 ----
    /// <summary>启用/禁用 EQ 页控件（由主页视图按快照的 equalizer 可用状态驱动）。</summary>
    void SetEqControlsEnabled(bool enabled);
    /// <summary>弹出「查找耳机」安全警告，返回用户是否确认继续。</summary>
    Task<bool> ShowFindWarningDialogAsync();
}
