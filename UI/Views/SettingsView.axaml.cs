using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using OppoPodsManager.Assets.Localization;
using OppoPodsManager.Control.Abstractions;
using OppoPodsManager.Control.Brands.Oppo.Models;
using OppoPodsManager.Control.Core.Models;
namespace OppoPodsManager.UI.Views;
public partial class SettingsView : PageView
{
    // 三级联动：品牌 → 子系列 → 机型
    private readonly ObservableCollection<string> _brandList = new();
    private readonly ObservableCollection<string> _seriesList = new();
    private readonly ObservableCollection<string> _modelList = new();
    private IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>> _brandTree
        = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>>();
    private string? _modelOverride;
    /// <summary>外壳在 Attach 前注入的机型目录。</summary>
    public ModelCatalog? ModelCatalog { get; set; }
    // 连接策略
    private bool _syncingConnectionStrategy;
    private string _priorityOptionsSignature = "";
    private sealed class PriorityDeviceOption
    {
        public string Address { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public bool IsAutomatic { get; init; }
        public override string ToString() => DisplayName;
    }
    public SettingsView()
    {
        InitializeComponent();
    }
    public override void Attach(
        ControlManager? controlManager,
        SettingsStore uiSettings,
        ApplicationLog? log,
        CommandDispatcher? commandDispatcher,
        FrontendState? frontendState,
        DesktopLinkService? desktopLinks)
    {
        base.Attach(controlManager, uiSettings, log, commandDispatcher, frontendState, desktopLinks);
        // 开关初始化（页面本地设置）
        CbTray.IsChecked = UiSettings.GetBool("TrayEnabled", false);
        CbAuto.IsChecked = UiSettings.GetBool("AutoStart", false);
        // 用 SetString/GetString 避免 SetBool(false) 删除条目导致默认值恢复
        var autoUpdate = UiSettings.GetBool("AutoCheckUpdate", true) ? "true" : "false";
        CbAutoUpdate.IsChecked = autoUpdate != "false";
        // 设备型号选择
        _brandTree = ModelCatalog?.BrandTree
            ?? new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<ModelDefinition>>>();
        CbBrand.ItemsSource = _brandList;
        CbSeries.ItemsSource = _seriesList;
        CbModel.ItemsSource = _modelList;
        _brandList.Add(LAutoDetect());
        foreach (var brand in _brandTree.Keys.OrderBy(b => b)) _brandList.Add(brand);
        _modelOverride = UiSettings.GetString("ModelOverride");
        if (string.IsNullOrEmpty(_modelOverride))
        {
            CbBrand.SelectedItem = LAutoDetect();
        }
        else
        {
            var location = ModelCatalog?.FindLocation(_modelOverride);
            CbBrand.SelectedItem = location?.Brand ?? LAutoDetect();
            if (location is not null)
            {
                _seriesList.Clear();
                _seriesList.Add(LAllSeries());
                foreach (var s in _brandTree[location.Brand].Keys.OrderBy(s => s)) _seriesList.Add(s);
                CbSeries.SelectedItem = location.Series;
                _modelList.Clear();
                _modelList.Add(LAllModels());
                foreach (var m in _brandTree[location.Brand][location.Series]
                    .Select(model => model.DisplayName)
                    .OrderBy(model => model))
                    _modelList.Add(m);
                CbModel.SelectedItem = _modelOverride;
            }
        }
        // 接线（按钮 Click 由 XAML 自动绑定）
        CbBrand.SelectionChanged += CbBrand_Changed;
        CbSeries.SelectionChanged += CbSeries_Changed;
        CbModel.SelectionChanged += CbModel_Changed;
        CbTray.IsCheckedChanged += CbTray_Changed;
        CbAuto.IsCheckedChanged += CbAuto_Changed;
        CbAutoUpdate.IsCheckedChanged += CbAutoUpdate_Changed;
        CbPriorityDevice.SelectionChanged += CbPriorityDevice_Changed;
    }
    public override void ApplySnapshot(BusinessSnapshot snapshot)
    {
        DiDeviceName.Text = DeviceText.DeviceName(snapshot.Identity?.DisplayName, snapshot.DeviceName);
        DiFirmware.Text = snapshot.Identity?.FirmwareVersion ?? "-";
        DiCodec.Text = snapshot.Identity?.Codec ?? "-";
        var nextPresentation = ControlManager?.ActiveManager?.Presentation;
        var nextModelName = nextPresentation?.IsKnownModel == true
            ? nextPresentation.ModelName
            : snapshot.Identity?.ModelName ?? snapshot.Identity?.DisplayName ?? snapshot.DeviceName;
        ModelNote.Text = snapshot.IsConnected && !string.IsNullOrWhiteSpace(nextModelName)
            ? string.Format(
                LanguageManager.Instance.GetString(_modelOverride is null
                    ? LanguageManager.Instance.Settings_ModelAutoDetected
                    : LanguageManager.Instance.Settings_ModelManualSet),
                nextModelName)
            : string.Empty;
    }
    /// <summary>导航到本页时刷新一次设备基本信息（与快照应用等价，纯本地）。</summary>
    public void RefreshDeviceInfo()
    {
        if (FrontendState?.Snapshot is { } snapshot) ApplySnapshot(snapshot);
    }
    // ====== 型号三级联动 ======
    private void CbBrand_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbBrand.SelectedItem is not string brand || brand == LAutoDetect())
        {
            _seriesList.Clear();
            _modelList.Clear();
            return;
        }
        if (!_brandTree.TryGetValue(brand, out var series)) return;
        _seriesList.Clear();
        _seriesList.Add(LAllSeries());
        foreach (var sn in series.Keys.OrderBy(x => x)) _seriesList.Add(sn);
        CbSeries.SelectedItem = LAllSeries();
    }
    private void CbSeries_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbBrand.SelectedItem is not string brand || !_brandTree.TryGetValue(brand, out var series)) return;
        var sn = CbSeries.SelectedItem as string ?? LAllSeries();
        _modelList.Clear();
        _modelList.Add(LAllModels());
        var models = sn == LAllSeries()
            ? series.SelectMany(kv => kv.Value).Select(model => model.DisplayName).Distinct().OrderBy(x => x).ToList()
            : series.TryGetValue(sn, out var list)
                ? list.Select(model => model.DisplayName).OrderBy(x => x).ToList()
                : new List<string>();
        foreach (var m in models) _modelList.Add(m);
        CbModel.SelectedItem = LAllModels();
    }
    private void CbModel_Changed(object? s, SelectionChangedEventArgs e)
    {
        if (CbModel.SelectedItem is string model && model != LAllModels())
        {
            Log?.Debug("UI", $"用户操作: 手动指定机型 -> {model}");
            _modelOverride = model;
            UiSettings.SetString("ModelOverride", model);
            SyncCaps();
        }
        else
        {
            _modelOverride = null;
            UiSettings.SetString("ModelOverride", null);
            SyncCaps();
        }
    }
    private void SyncCaps() => ControlManager?.SetManualModel(_modelOverride);
    private static string LAutoDetect() => LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_AutoDetect);
    private static string LAllSeries() => LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_AllSeries);
    private static string LAllModels() => LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_AllModels);
    // ====== 本机开关 ======
    private void CbTray_Changed(object? s, RoutedEventArgs e)
    {
        var on = CbTray.IsChecked == true;
        UiSettings.SetBool("TrayEnabled", on);
        Log?.Debug("UI", $"设置: 关闭到托盘 -> {on}");
    }
    private void CbAuto_Changed(object? s, RoutedEventArgs e)
    {
        var on = CbAuto.IsChecked == true;
        UiSettings.SetBool("AutoStart", on);
        Log?.Debug("UI", $"设置: 开机自启 -> {on}");
    }
    private void CbAutoUpdate_Changed(object? s, RoutedEventArgs e)
    {
        var on = CbAutoUpdate.IsChecked == true;
        UiSettings.SetBool("AutoCheckUpdate", on);
        Log?.Debug("UI", $"设置: 自动检查更新 -> {on}");
    }
    // ====== 工具按钮 ======
    private async void BtnCheckUpdate_Click(object? s, RoutedEventArgs e)
    {
        Log?.Debug("UI", "用户操作: 手动检查更新");
        BtnCheckUpdate.IsEnabled = false;
        BtnCheckUpdate.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_Checking);
        try { if (Host is not null) await Host.CheckForUpdatesAsync(); }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            BtnCheckUpdate.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_CheckUpdate);
        }
    }
    private void BtnFeedback_Click(object? s, RoutedEventArgs e)
        => _ = Host?.OpenFeedbackAsync();
    private void BtnViewLog_Click(object? s, RoutedEventArgs e)
        => Host?.RequestNavigate("log");
    private void About_Click(object? s, RoutedEventArgs e)
        => Host?.RequestNavigate("about");
    private void BtnRestoreHiddenDevices_Click(object? s, RoutedEventArgs e)
    {
        ControlManager?.RestoreHiddenMultiDevices();
        Host?.ResyncMultiDeviceList();
        RefreshRestoreHiddenDevicesButton();
        _ = CommandDispatcher?.RunAsync("刷新多设备列表",
            manager => manager.RefreshMultiDeviceAsync(CancellationToken.None));
        Log?.Debug("UI", "已清除本地隐藏设备策略并同步多设备状态");
    }
    // ====== 连接策略卡 ======
    internal void UpdateConnectionStrategyVisibility(bool visible)
    {
        DiConnectionStrategyCard.IsVisible = visible;
        PriorityDevicePanel.IsVisible = visible;
    }
    internal void SyncConnectionStrategy(
        IBrandManager manager,
        MultiDeviceSnapshot multiDevice,
        IReadOnlyList<ConnectedDeviceSnapshot> connectedDevices)
    {
        var canManage = manager.Presentation.CanManageMultiDevice;
        DiConnectionStrategyCard.IsVisible = canManage;
        PriorityDevicePanel.IsVisible = canManage;
        _syncingConnectionStrategy = true;
        try
        {
            if (!canManage)
            {
                CbPriorityDevice.Items.Clear();
                CbPriorityDevice.SelectedItem = null;
                _priorityOptionsSignature = "";
                return;
            }
            var signature = string.Join("|", connectedDevices.Select(device =>
                $"{device.Address};{device.Name};{device.IsCurrent}"))
                + $"|auto={multiDevice.IsAutomaticPriority}|priority={multiDevice.PriorityDeviceAddress}";
            if (signature != _priorityOptionsSignature)
            {
                _priorityOptionsSignature = signature;
                CbPriorityDevice.Items.Clear();
                CbPriorityDevice.Items.Add(new PriorityDeviceOption
                {
                    IsAutomatic = true,
                    DisplayName = LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_Automatic)
                });
                foreach (var device in connectedDevices)
                    CbPriorityDevice.Items.Add(new PriorityDeviceOption
                    {
                        Address = device.Address,
                        DisplayName = DeviceText.MultiDeviceName(device)
                    });
            }
            var selected = multiDevice.IsAutomaticPriority
                ? CbPriorityDevice.Items.OfType<PriorityDeviceOption>().FirstOrDefault(option => option.IsAutomatic)
                : CbPriorityDevice.Items.OfType<PriorityDeviceOption>().FirstOrDefault(option =>
                    string.Equals(option.Address, multiDevice.PriorityDeviceAddress, StringComparison.OrdinalIgnoreCase));
            CbPriorityDevice.SelectedItem = selected;
            CbPriorityDevice.PlaceholderText = selected is null && !multiDevice.IsAutomaticPriority
                ? LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_PriorityUnavailable)
                : LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_PriorityHint);
        }
        finally
        {
            _syncingConnectionStrategy = false;
        }
    }
    internal void ResetPrioritySignature() => _priorityOptionsSignature = "";
    private void CbPriorityDevice_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (CbPriorityDevice.SelectedItem is not PriorityDeviceOption option
            || FrontendState?.Snapshot.IsConnected != true)
            return;
        if (_syncingConnectionStrategy) return;
        ApplyPrioritySelection(option);
    }
    private void ApplyPrioritySelection(PriorityDeviceOption option)
    {
        _ = CommandDispatcher?.RunAsync("多设备优先级", manager =>
            manager.SetMultiDevicePriorityAsync(
                option.IsAutomatic,
                option.IsAutomatic ? null : option.Address,
                CancellationToken.None));
    }
    internal void RefreshRestoreHiddenDevicesButton()
    {
        var count = ControlManager?.GetHiddenMultiDeviceCount() ?? 0;
        BtnRestoreHiddenDevices.IsEnabled = count > 0;
        BtnRestoreHiddenDevices.Content = count > 0
            ? string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.MultiDevice_RestoreHidden), count)
            : LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_RestoreHiddenDevices);
    }
}
