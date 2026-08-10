using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using OppoPodsManager.Control.Updates;
using UpdateToastAction = OppoPodsManager.UI.Toast.UpdateToastAction;

namespace OppoPodsManager.UI.MainWindow;public partial class MainWindow{    private async Task<string?> ShowPromptDialog(string title, string defaultText = "", string hint = "")
    {
        _promptTcs = new TaskCompletionSource<string?>();
        _confirmTcs = null;

        DialogTitle.Text = title;
        DialogMessage.Text = string.IsNullOrEmpty(hint) ? LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_InputPresetName) : hint;
        DialogInput.IsVisible = true;
        DialogInput.Text = defaultText;
        DialogCancelBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Cancel);
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Save);
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;
        _logManager?.Debug("UI", $"对话框: 打开输入框 -> {title}");
        DialogInput.Focus();
        DialogInput.SelectAll();

        return await _promptTcs.Task;
    }
    private async Task<bool> ShowConfirmDialog(string title, string message)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogInput.IsVisible = false;
        DialogCancelBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_Cancel);
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_ConfirmDelete);
        DialogConfirmBtn.Background = new SolidColorBrush(Color.Parse("#CCE81123"));
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;
        _logManager?.Debug("UI", $"对话框: 打开确认框 -> {title}");

        return await _confirmTcs.Task;
    }

    private void DialogSkip_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DialogSkipBtn.Content is string label && label == "GitLab")
        {
            DialogOverlay_Close();
            _confirmTcs?.TrySetResult(false);
            ExportFeedback("https://jihulab.com/zhaoyi-ya-group/oppo-pods-manager/-/work_items/new");
            return;
        }
        _updateCoordinator?.SkipVersion(_updatePendingVersion);
        DialogOverlay_Close();
        _confirmTcs?.TrySetResult(false);
    }

    private void DialogOverlay_Close()
    {
        DialogOverlay.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
        DialogMirrorBtn.IsVisible = false;
    }

    private void DialogMirror_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "对话框: 国内下载");
        DialogOverlay_Close();
        _confirmTcs?.TrySetResult(false);
        _updateCoordinator?.TryOpenDownload("mirror", UpdateCoordinator.MirrorDownloadUrl);
    }

    private void DialogCancel_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "对话框: 取消");
        DialogOverlay_Close();
        _promptTcs?.TrySetResult(null);
        _confirmTcs?.TrySetResult(false);
    }

    private void DialogConfirm_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _logManager?.Debug("UI", "对话框: 确认");
        DialogOverlay_Close();

        if (_promptTcs != null)
        {
            var text = DialogInput.Text?.Trim();
            _promptTcs.TrySetResult(string.IsNullOrEmpty(text) ? null : text);
        }
        else if (_confirmTcs != null)
        {
            _confirmTcs.TrySetResult(true);
        }
    }
    private void HandleUpdateToastAction(UpdateToastAction action, string serverVersion, string downloadUrl)
    {
        if (action == UpdateToastAction.Skip)
        {
            _updateCoordinator?.SkipVersion(serverVersion);
            return;
        }

        if (action == UpdateToastAction.MirrorDownload)
        {
            _updateCoordinator?.TryOpenDownload("mirror", UpdateCoordinator.MirrorDownloadUrl);
            return;
        }

        if (action == UpdateToastAction.Download)
        {
            _updateCoordinator?.TryOpenDownload("github", downloadUrl);
        }
    }

    private async Task ShowCheckResultDialog(string msg, string? title = null)
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = title ?? LanguageManager.Instance.GetString(LanguageManager.Instance.Settings_CheckUpdate);
        DialogMessage.Text = msg;
        DialogInput.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
        DialogCancelBtn.IsVisible = false;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_OK);
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        await _confirmTcs.Task;
    }

    private async Task<bool> ShowUpdateDialog(string newVersion, string content = "")
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Toast_NewVersion);
        if (string.IsNullOrEmpty(content))
            DialogMessage.Text = string.Format(
                LanguageManager.Instance.GetString(LanguageManager.Instance.Update_MessageNoContent), newVersion, AppInfo.VersionLabel);
        else
            DialogMessage.Text = string.Format(
                LanguageManager.Instance.GetString(LanguageManager.Instance.Update_MessageWithContent), newVersion, AppInfo.VersionLabel) + content;
        DialogInput.IsVisible = false;
        DialogCancelBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_RemindLater);
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogSkipBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_SkipVersion);
        DialogSkipBtn.Background = Brushes.Transparent;
        DialogSkipBtn.IsVisible = true;
        DialogMirrorBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_MirrorDownload);
        DialogMirrorBtn.Background = Brushes.Transparent;
        DialogMirrorBtn.IsVisible = true;
        DialogConfirmBtn.Content = LanguageManager.Instance.GetString(LanguageManager.Instance.Dialog_GitHubDownload);
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        _updatePendingVersion = newVersion;

        return await _confirmTcs.Task;
    }
    private async Task<bool> ShowFindWarningDialog()
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = "安全警告";
        DialogMessage.Text = "警告：使用“查找耳机”功能时，请勿将耳机戴在耳朵中。\n耳机响铃音量较大，戴在耳内可能造成永久性听力损伤。";
        DialogInput.IsVisible = false;
        DialogSkipBtn.IsVisible = false;
        DialogMirrorBtn.IsVisible = false;
        DialogCancelBtn.Content = "取消";
        DialogCancelBtn.Background = Brushes.Transparent;
        DialogCancelBtn.IsVisible = true;
        DialogConfirmBtn.Content = "我已知晓，继续";
        DialogConfirmBtn.Background = Brushes.Transparent;
        DialogConfirmBtn.IsVisible = true;
        DialogOverlay.IsVisible = true;

        return await _confirmTcs.Task;
    }
}
