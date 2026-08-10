using Avalonia.Controls;
using OppoPodsManager.Control.Updates;

namespace OppoPodsManager.UI.MainWindow;
public partial class MainWindow
{
    private async Task DoCheckUpdateAsync(bool silent = false)
    {
        if (_updateCoordinator is null)
        {
            _logManager?.Debug("UI", "检查更新跳过：更新协调器尚未注入。");
            return;
        }

        // 计算当前界面文化，交给更新服务请求本地化的更新说明。
        var uiLang = LanguageManager.ResolveCulture(_uiSettings.GetString("Language")).Name;
        var result = await _updateCoordinator.CheckAsync(
            AppInfo.VersionLabel,
            uiLang,
            CancellationToken.None,
            respectSkippedVersion: silent);

        if (result.Status is UpdateCheckStatus.Canceled or UpdateCheckStatus.Skipped)
            return;

        if (result.Status == UpdateCheckStatus.UpToDate)
        {
            if (!silent)
                await ShowCheckResultDialog(string.Format(
                    LanguageManager.Instance.GetString(LanguageManager.Instance.Update_UpToDate),
                    AppInfo.VersionLabel));
            return;
        }

        if (!result.IsAvailable || string.IsNullOrWhiteSpace(result.Version))
        {
            if (!silent)
                await ShowCheckResultDialog(GetUpdateFailureText(result.Status));
            return;
        }

        var serverVersion = result.Version;
        if (!silent)
        {
            var go = await ShowUpdateDialog(serverVersion, result.Content);
            if (go)
                _updateCoordinator.TryOpenDownload("github", result.DownloadUrl);
            return;
        }

        var shouldUseToast = !IsVisible || WindowState == WindowState.Minimized || !IsActive;
        if (shouldUseToast)
        {
            var action = await ToastWindow.ShowUpdateAsync(serverVersion);
            HandleUpdateToastAction(action, serverVersion, result.DownloadUrl);
        }
        else
        {
            var go = await ShowUpdateDialog(serverVersion, result.Content);
            if (go)
                _updateCoordinator.TryOpenDownload("github", result.DownloadUrl);
        }
    }
    private static string GetUpdateFailureText(UpdateCheckStatus status)
        => LanguageManager.Instance.GetString(status switch
        {
            UpdateCheckStatus.Timeout => LanguageManager.Instance.Update_Timeout,
            UpdateCheckStatus.NetworkError => LanguageManager.Instance.Update_ConnectFailed,
            UpdateCheckStatus.ParseError => LanguageManager.Instance.Update_ParseError,
            _ => LanguageManager.Instance.Update_NetworkError
        });
}
