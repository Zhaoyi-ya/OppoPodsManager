using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using OppoPodsManager.Control.Subsystems.Logging;

namespace OppoPodsManager.UI.Views;

public partial class LogView : PageView
{
    private readonly ObservableCollection<string> _renderedEntries = new();
    private int _renderedVersion = -1;
    private DispatcherTimer? _refreshTimer;
    private bool _autoScroll = true;
    private bool _scrollPending;
    private ScrollViewer? _scrollViewer;

    public LogView()
    {
        InitializeComponent();
        LbLog.ItemsSource = _renderedEntries;
    }

    /// <summary>进入日志页时调用：定位内部滚动视图并启动实时刷新。</summary>
    public void Start()
    {
        if (_scrollViewer is null)
        {
            Dispatcher.UIThread.Post(() =>
            {
                _scrollViewer = FindScrollViewer(LbLog);
                if (_scrollViewer is not null)
                {
                    _scrollViewer.ScrollChanged += (_, _) =>
                    {
                        var sv = _scrollViewer;
                        var atBottom = sv.Offset.Y >= sv.Extent.Height - sv.Viewport.Height - 1;
                        _autoScroll = atBottom;
                    };
                }
            }, DispatcherPriority.Loaded);
        }

        _refreshTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => RefreshLogView());
        _refreshTimer.Start();
        RefreshLogView();
    }

    /// <summary>离开日志页时调用：停止刷新并复位自动跟随。</summary>
    public void Stop()
    {
        _refreshTimer?.Stop();
        _scrollPending = false;
        _autoScroll = true;
    }

    private void RefreshLogView()
    {
        var lines = (Log ?? ApplicationLog.Current)?.Snapshot()
            .Select(entry => entry.ToString())
            .ToArray() ?? [];
        var version = lines.Length;
        if (version == _renderedVersion && _renderedEntries.Count > 0)
            return;

        _renderedEntries.Clear();
        foreach (var line in lines)
            _renderedEntries.Add(line);
        _renderedVersion = version;

        // 自动跟随最新日志
        if (_autoScroll && _scrollViewer is not null && !_scrollPending)
        {
            _scrollPending = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scrollPending = false;
                if (_scrollViewer is not null)
                    _scrollViewer.Offset = new Vector(_scrollViewer.Offset.X,
                        _scrollViewer.Extent.Height);
            }, DispatcherPriority.Loaded);
        }
    }

    private async void BtnLogExport_Click(object? s, RoutedEventArgs e)
    {
        var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
        if (storage is null || !storage.CanSave)
            return;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = LanguageManager.Instance.GetString(LanguageManager.Instance.Log_ExportTitle),
            DefaultExtension = "zip",
            ShowOverwritePrompt = true,
            SuggestedFileName = $"OPPOPods_logs_{DateTime.Now:yyyyMMdd_HHmmss}.zip"
        });
        if (file is null)
            return;

        var log = Log ?? ApplicationLog.Current;
        if (log is null)
            return;

        if (!log.TryExportZip(file.Path.LocalPath, out var exportError))
        {
            if (Host is { } host)
                await host.ShowCheckResultDialogAsync(
                    string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ExportError),
                        exportError ?? string.Empty),
                    LanguageManager.Instance.GetString(LanguageManager.Instance.Log_ExportZip));
            return;
        }

        if (Host is { } host2)
            await host2.ShowCheckResultDialogAsync(
                string.Format(LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ExportSuccess), file.Path.LocalPath),
                LanguageManager.Instance.GetString(LanguageManager.Instance.Log_ExportZip));
    }

    private void BtnLogBack_Click(object? s, RoutedEventArgs e)
    {
        Stop();
        Host?.RequestNavigate("settings");
    }

    private static ScrollViewer? FindScrollViewer(Visual visual)
    {
        if (visual is ScrollViewer sv) return sv;
        foreach (var child in visual.GetVisualChildren())
        {
            var found = FindScrollViewer(child);
            if (found is not null) return found;
        }
        return null;
    }

    public override void ApplySnapshot(BusinessSnapshot snapshot) { }
}
