using Avalonia.Input;
using Avalonia.Interactivity;namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    private void ShowPage(string page)
    {
        if (_currentPage != page)
        {
            _logManager?.Debug("UI", $"页面切换: {_currentPage} -> {page}");
            _currentPage = page;
        }
        MainPanel.IsVisible = page == "home";
        // 主页始终保留降噪卡片，未连接设备时由占位内容填充。
        if (page == "home")
            HomeView?.SetAncPanelVisible(true);
        EqPanel.IsVisible = page == "eq";
        if (page == "eq")
            _ = _commandDispatcher?.RunAsync("刷新自定义 EQ", manager => manager.RefreshCustomEqualizersAsync(CancellationToken.None));
        DeviceInfoPanel.IsVisible = page == "deviceinfo";
        PersonalPanel.IsVisible = page == "personal";
        SettingsPanel.IsVisible = page == "settings";
        LogPanel.IsVisible = page == "log";
        AboutPanel.IsVisible = page == "about";

        NavHome.Classes.Remove("selected");
        NavEq.Classes.Remove("selected");
        NavPersonal.Classes.Remove("selected");
        NavSettings.Classes.Remove("selected");

        if (page == "home") NavHome.Classes.Add("selected");
        else if (page == "eq") NavEq.Classes.Add("selected");
        else if (page == "personal") NavPersonal.Classes.Add("selected");
        else NavSettings.Classes.Add("selected");

        if (page != "log")
            LogView.Stop();
        if (page == "deviceinfo" || page == "settings") SettingsView?.RefreshDeviceInfo();
        if (page == "eq" && _frontendState is not null)
            EqView?.ApplySnapshot(_frontendState.Snapshot);
        if (page == "log") LogView.Start();
    }
    private void BtnViewLog_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "日志面板: 打开");
        ShowPage("log");
    }
    private void NavHome_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("home");
    private void NavEq_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("eq");
    private void NavPersonal_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("personal");
    private void NavSettings_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e) => ShowPage("settings");
    private void About_Click(object? s, RoutedEventArgs e) => ShowPage("about");

}
