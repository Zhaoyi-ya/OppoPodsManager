using OppoPodsManager.Assets.UserSettings;

namespace OppoPodsManager.Control.Subsystems.Updates;

// 统一管理更新查询、跳过版本和下载渠道，避免窗口直接持有更新服务实现。
public sealed class UpdateCoordinator : IDisposable
{
    public const string MirrorDownloadUrl = UpdateService.MirrorDownloadUrl;

    private readonly UpdateService _service;

    public UpdateCoordinator(SettingsManager? settings = null)
        : this(new UpdateService(settings))
    {
    }

    public UpdateCoordinator(UpdateService service)
    {
        _service = service;
    }

    // 查询远程更新并返回已经完成解析和版本比较的结果。
    public Task<UpdateCheckResult> CheckAsync(
        string localVersion,
        string language,
        CancellationToken cancellationToken,
        bool respectSkippedVersion = false)
        => _service.CheckAsync(localVersion, language, cancellationToken, respectSkippedVersion);

    // 记录用户忽略的版本号。
    public void SkipVersion(string version) => _service.SkipVersion(version);

    // 记录下载渠道并打开更新地址。
    public bool TryOpenDownload(string channel, string url)
        => _service.TryOpenDownload(channel, url);

    public void Dispose() => _service.Dispose();
}
