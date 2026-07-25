using System;
using System.Diagnostics;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using OppoPodsManager.Localization;

namespace OppoPodsManager;

public partial class MainWindow
{
    // ===== 版本更新检查 =====

    // 正式更新接口（生产环境）
    private const string UPDATE_API = "https://oppopods.zhaoyi.fun/api/update/latest";
    private const string DOWNLOAD_URL = "https://github.com/Zhaoyi-ya/OppoPodsManager/releases/latest";
    private const string DOWNLOAD_MIRROR_URL = "https://www.zhaoyi.fun/index.php/archives/7/";
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private async void BtnCheckUpdate_Click(object? s, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Log.D("UI", "用户操作: 手动检查更新");
        BtnCheckUpdate.IsEnabled = false;
        BtnCheckUpdate.Content = _checking;
        try { await DoCheckUpdateAsync(silent: false); }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
            BtnCheckUpdate.Content = _checkUpdate;
        }
    }

    private async Task CheckForUpdateAsync()
    {
        await Task.Delay(5000);
        if (SettingsManager.GetString("AutoCheckUpdate") == "false") return;
        await DoCheckUpdateAsync(silent: true);
    }

    private async Task DoCheckUpdateAsync(bool silent = false)
    {
        try
        {
            // 发送 Accept-Language，让更新服务器按当前界面语言返回翻译后的更新说明。
            // 复用与 App 启动一致的解析逻辑（LanguageManager.ResolveCulture），确保与用户在 App 内看到的语言一致；
            // 放在每次请求时设置，以覆盖运行期间切换语言的场景。
            var uiLang = LanguageManager.ResolveCulture(SettingsManager.GetString("Language")).Name;
            _http.DefaultRequestHeaders.Remove("Accept-Language");
            if (!string.IsNullOrEmpty(uiLang))
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", uiLang);

            Log.D("UPDATE", $"开始检查更新，silent={silent}, api={UPDATE_API}, lang={uiLang}");
            var resp = await _http.GetStringAsync(UPDATE_API);
            using var doc = System.Text.Json.JsonDocument.Parse(resp);
            var json = doc.RootElement;
            var serverVersion = json.GetProperty("version").GetString();
            var content = json.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            var downloadUrl = json.TryGetProperty("download_url", out var u) ? u.GetString() ?? DOWNLOAD_URL : DOWNLOAD_URL;
            Log.D("UPDATE", $"检查更新响应: serverVersion={serverVersion ?? "<null>"}, localVersion={VersionText.Text}");

            if (string.IsNullOrEmpty(serverVersion) || !IsNewerThan(serverVersion, VersionText.Text!))
            {
                Log.D("UPDATE", "当前已是最新版本");
                if (!silent) await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ShowCheckResultDialog(string.Format(
                        LanguageManager.Instance.GetString(LanguageManager.Instance.Update_UpToDate), VersionText.Text)));
                return;
            }

            // 自动检查时才跳过已跳过的版本
            if (silent)
            {
                var skipped = SettingsManager.GetString("SkippedVersion");
                if (serverVersion == skipped)
                {
                    Log.D("UPDATE", $"自动检查跳过已忽略版本: {serverVersion}");
                    return;
                }
            }

            Log.D("UPDATE", $"发现新版本: {serverVersion}");
            if (!silent)
            {
                var go = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ShowUpdateDialog(serverVersion, content));
                if (go)
                    Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
                return;
            }

            var shouldUseToast = !IsVisible || WindowState == WindowState.Minimized || !IsActive;
            if (shouldUseToast)
            {
                var action = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ToastWindow.ShowUpdateAsync(serverVersion));
                HandleUpdateToastAction(action, serverVersion, downloadUrl);
            }
            else
            {
                var go = await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ShowUpdateDialog(serverVersion, content));
                if (go)
                    Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
            }
        }
        catch (TaskCanceledException ex) when (!silent)
        {
            Log.Ex("UPDATE", "检查更新请求超时或被取消", ex);
            await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Update_Timeout)));
        }
        catch (HttpRequestException ex) when (!silent)
        {
            Log.Ex("UPDATE", "检查更新网络请求失败", ex);
            await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Update_ConnectFailed)));
        }
        catch (System.Text.Json.JsonException ex) when (!silent)
        {
            Log.Ex("UPDATE", "检查更新响应解析失败", ex);
            await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Update_ParseError)));
        }
        catch (Exception ex)
        {
            Log.Ex("UPDATE", "检查更新失败", ex);
            if (!silent) await Dispatcher.UIThread.InvokeAsync(async () =>
                await ShowCheckResultDialog(LanguageManager.Instance.GetString(LanguageManager.Instance.Update_NetworkError)));
        }
    }

    private static void HandleUpdateToastAction(UpdateToastAction action, string serverVersion, string downloadUrl)
    {
        if (action == UpdateToastAction.Skip)
        {
            SettingsManager.SetString("SkippedVersion", serverVersion);
            Log.D("UPDATE", $"用户跳过版本: {serverVersion}");
            return;
        }

        if (action == UpdateToastAction.MirrorDownload)
        {
            Log.D("UPDATE", $"用户前往国内下载: {DOWNLOAD_MIRROR_URL}");
            Process.Start(new ProcessStartInfo(DOWNLOAD_MIRROR_URL) { UseShellExecute = true });
            return;
        }

        if (action == UpdateToastAction.Download)
        {
            Log.D("UPDATE", $"用户前往 GitHub 下载: {downloadUrl}");
            Process.Start(new ProcessStartInfo(downloadUrl) { UseShellExecute = true });
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

    /// <summary>比较版本号：server &gt; local 返回 true。支持 v1.0.6 &gt; v1.0.5。</summary>
    private static bool IsNewerThan(string server, string local)
    {
        // 去掉 v/V 前缀
        var sv = server.StartsWith('v') || server.StartsWith('V') ? server[1..] : server;
        var lv = local.StartsWith('v') || local.StartsWith('V') ? local[1..] : local;

        if (System.Version.TryParse(sv, out var sVer) && System.Version.TryParse(lv, out var lVer))
            return sVer > lVer;

        // 非标准格式（如 v1.0.6beta）回退到字符串比较，抛日志提醒
        Log.D("UPDATE", $"非标准版本号格式: server={server} local={local}");
        return string.Compare(sv, lv, StringComparison.Ordinal) > 0;
    }

    private async Task<bool> ShowUpdateDialog(string newVersion, string content = "")
    {
        _confirmTcs = new TaskCompletionSource<bool>();
        _promptTcs = null;

        DialogTitle.Text = LanguageManager.Instance.GetString(LanguageManager.Instance.Toast_NewVersion);
        if (string.IsNullOrEmpty(content))
            DialogMessage.Text = string.Format(
                LanguageManager.Instance.GetString(LanguageManager.Instance.Update_MessageNoContent), newVersion, VersionText.Text);
        else
            DialogMessage.Text = string.Format(
                LanguageManager.Instance.GetString(LanguageManager.Instance.Update_MessageWithContent), newVersion, VersionText.Text) + content;
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
}
