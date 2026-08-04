using OppoPodsManager.Assets.UserSettings;

namespace OppoPodsManager.Assets.VisualAssets;

// 管理当前背景选择和背景历史持久化，不依赖窗口控件。
public sealed class BackgroundSelectionService
{
    private readonly SettingsStore _settings;
    private readonly BackgroundImageManager _images;
    private readonly List<string> _history;

    public BackgroundSelectionService(SettingsStore settings, BackgroundImageManager images)
    {
        _settings = settings;
        _images = images;
        SelectedKey = _settings.GetString("BgCurrent") ?? "default";
        _history = _settings.GetStringList("BgHistory").ToList();
    }

    // 返回当前选中的默认背景或文件路径。
    public string SelectedKey { get; private set; }

    // 返回已经持久化的背景历史，只读给界面生成缩略图。
    public IReadOnlyList<string> History => _history;

    // 保存当前背景选择，并统一把默认背景转换为空路径。
    public void Select(string? key)
    {
        SelectedKey = string.IsNullOrWhiteSpace(key) ? "default" : key;
        _settings.SetString("BgCurrent", SelectedKey == "default" ? null : SelectedKey);
    }

    // 复制新背景、更新历史并切换到最新背景。
    public bool Add(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;

        var updated = _images.AddToHistory(_history, sourcePath);
        _history.Clear();
        _history.AddRange(updated);
        SaveHistory();
        if (_history.Count == 0)
            return false;

        Select(_history[0]);
        return true;
    }

    // 删除背景历史项并清理资源；删除当前背景时恢复默认背景。
    public bool Remove(string path)
    {
        var updated = _images.RemoveFromHistory(_history, path);
        if (updated.Count == _history.Count)
            return false;

        _history.Clear();
        _history.AddRange(updated);
        SaveHistory();
        if (string.Equals(SelectedKey, path, StringComparison.OrdinalIgnoreCase))
            Select("default");
        return true;
    }

    private void SaveHistory()
        => _settings.SetStringList("BgHistory", _history);
}
