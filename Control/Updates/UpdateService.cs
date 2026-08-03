using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using OppoPodsManager.Assets.UserSettings;
using OppoPodsManager.Control.Logging;

namespace OppoPodsManager.Control.Updates;

// 负责访问更新服务并把响应转换成界面可消费的更新结果。
public sealed class UpdateService : IDisposable
{
    private const string UpdateApi = "https://oppopods.zhaoyi.fun/api/update/latest";
    private const string DefaultDownloadUrl = "https://github.com/Zhaoyi-ya/OppoPodsManager/releases/latest";
    public const string MirrorDownloadUrl = "https://www.zhaoyi.fun/index.php/archives/7/";
    private readonly SettingsManager? _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public UpdateService(SettingsManager? settings = null)
    {
        _settings = settings;
    }

    // 查询远端版本信息并在控制层完成版本比较。
    public async Task<UpdateCheckResult> CheckAsync(
        string localVersion,
        string language,
        CancellationToken cancellationToken,
        bool respectSkippedVersion = false)
    {
        _http.DefaultRequestHeaders.Remove("Accept-Language");
        if (!string.IsNullOrWhiteSpace(language))
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", language);

        ApplicationLog.Current?.Debug("Update", "开始检查更新：api=" + UpdateApi + "，language=" + language + "。");
        try
        {
            var response = await _http.GetStringAsync(UpdateApi, cancellationToken);
            using var document = JsonDocument.Parse(response);
            var root = document.RootElement;
            var version = root.GetProperty("version").GetString();
            if (string.IsNullOrWhiteSpace(version))
            {
                ApplicationLog.Current?.Debug("Update", "更新查询完成：服务端没有提供有效版本号。");
                return UpdateCheckResult.UpToDate();
            }

            var content = root.TryGetProperty("content", out var contentValue)
                ? contentValue.GetString() ?? string.Empty
                : string.Empty;
            var downloadUrl = root.TryGetProperty("download_url", out var urlValue)
                ? urlValue.GetString() ?? DefaultDownloadUrl
                : DefaultDownloadUrl;
            var available = IsNewerThan(version, localVersion);
            if (!available)
            {
                ApplicationLog.Current?.Debug("Update", "更新查询完成：server=" + version + "，local=" + localVersion + "，当前已是最新版本。");
                return new UpdateCheckResult(UpdateCheckStatus.UpToDate, version, content, downloadUrl);
            }

            if (respectSkippedVersion && string.Equals(version, _settings?.Current.SkippedVersion, StringComparison.Ordinal))
            {
                ApplicationLog.Current?.Debug("Update", "自动检查跳过已忽略版本：version=" + version + "。");
                return new UpdateCheckResult(UpdateCheckStatus.Skipped, version, content, downloadUrl);
            }

            ApplicationLog.Current?.Info("Update", "发现新版本：server=" + version + "，local=" + localVersion + "。");
            return new UpdateCheckResult(UpdateCheckStatus.Available, version, content, downloadUrl);
        }
        catch (TaskCanceledException exception)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                ApplicationLog.Current?.Info("Update", "检查更新已取消。");
                return UpdateCheckResult.Canceled();
            }

            ApplicationLog.Current?.Error("Update", "检查更新请求超时。", exception);
            return UpdateCheckResult.Failed(UpdateCheckStatus.Timeout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ApplicationLog.Current?.Info("Update", "检查更新已取消。");
            return UpdateCheckResult.Canceled();
        }
        catch (HttpRequestException exception)
        {
            ApplicationLog.Current?.Error("Update", "检查更新网络请求失败。", exception);
            return UpdateCheckResult.Failed(UpdateCheckStatus.NetworkError);
        }
        catch (JsonException exception)
        {
            ApplicationLog.Current?.Error("Update", "检查更新响应解析失败。", exception);
            return UpdateCheckResult.Failed(UpdateCheckStatus.ParseError);
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Update", "检查更新失败。", exception);
            return UpdateCheckResult.Failed(UpdateCheckStatus.Failed);
        }
    }

    // 保存用户忽略的版本，并记录更新策略变化。
    public void SkipVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
            return;

        try
        {
            _settings?.Update(settings => settings with { SkippedVersion = version });
            ApplicationLog.Current?.Info("Update", "用户跳过版本：version=" + version + "。");
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Update", "保存跳过版本失败：version=" + version + "。", exception);
        }
    }

    // 记录用户选择的更新下载渠道，具体打开链接仍由桌面界面执行。
    public void RecordDownloadSelection(string channel, string url)
    {
        ApplicationLog.Current?.Info("Update", "用户选择下载渠道：" + channel + "，url=" + url + "。");
    }

    // 记录下载渠道并打开对应的更新地址，统一处理桌面启动异常。
    public bool TryOpenDownload(string channel, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            ApplicationLog.Current?.Error("Update", "打开更新地址失败：地址为空。");
            return false;
        }

        try
        {
            RecordDownloadSelection(channel, url);
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            ApplicationLog.Current?.Info("Update", "已打开更新地址：channel=" + channel + "。");
            return true;
        }
        catch (Exception exception)
        {
            ApplicationLog.Current?.Error("Update", "打开更新地址失败：channel=" + channel + "，url=" + url + "。", exception);
            return false;
        }
    }

    // 比较标准版本号，并兼容服务端返回的 v 前缀和非标准版本文本。
    public static bool IsNewerThan(string server, string local)
    {
        var serverValue = TrimVersionPrefix(server);
        var localValue = TrimVersionPrefix(local);
        if (Version.TryParse(serverValue, out var serverVersion)
            && Version.TryParse(localValue, out var localVersion))
            return serverVersion > localVersion;

        ApplicationLog.Current?.Debug("Update", $"使用文本比较非标准版本号：server={server}，local={local}。");
        return string.Compare(serverValue, localValue, StringComparison.Ordinal) > 0;
    }

    public void Dispose() => _http.Dispose();

    private static string TrimVersionPrefix(string value)
        => value.StartsWith('v') || value.StartsWith('V') ? value[1..] : value;
}

// 表示更新服务已经完成解析和版本比较的结果。
public enum UpdateCheckStatus
{
    UpToDate,
    Available,
    Skipped,
    Timeout,
    NetworkError,
    ParseError,
    Failed,
    Canceled
}

// 表示更新服务已经完成请求、解析和版本比较的结果。
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? Version,
    string Content,
    string DownloadUrl)
{
    public bool IsAvailable => Status == UpdateCheckStatus.Available;

    public static UpdateCheckResult UpToDate()
        => new(UpdateCheckStatus.UpToDate, null, string.Empty, string.Empty);

    public static UpdateCheckResult Failed(UpdateCheckStatus status)
        => new(status, null, string.Empty, string.Empty);

    public static UpdateCheckResult Canceled()
        => new(UpdateCheckStatus.Canceled, null, string.Empty, string.Empty);
}
