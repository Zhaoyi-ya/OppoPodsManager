using System.Globalization;

namespace OppoPodsManager.Assets.Localization;

// 保存语言选择项，并为设置界面提供显示名称。
public sealed record LanguageOption(string CultureCode, string DisplayName)
{
    // 空文化码表示跟随系统语言。
    public bool IsAutomatic => string.IsNullOrEmpty(CultureCode);

    public override string ToString() => DisplayName;
}

// 提供代码和 AXAML 共用的本地化访问接口。
public sealed class LanguageManager
{
    // 表示跟随系统语言的设置值。
    public const string AutomaticCultureCode = "";

    private const string DefaultCultureCode = "zh-Hans";
    private static readonly Lazy<LanguageManager> LazyInstance = new(() => new LanguageManager());
    private readonly object _gate = new();
    private readonly Dictionary<string, LocalizedText> _texts = new(StringComparer.Ordinal);

    private LanguageManager()
    {
        TranslationCatalog.LanguageChanged += NotifyLanguageChanged;
    }

    // 返回全局唯一的本地化管理器实例。
    public static LanguageManager Instance => LazyInstance.Value;

    // 返回用户可选择的语言列表。
    public static IReadOnlyList<LanguageOption> GetAvailableLanguages()
        =>
        [
            new(AutomaticCultureCode, Instance.GetString(Instance.Personal_LanguageAuto)),
            new(DefaultCultureCode, CultureInfo.GetCultureInfo(DefaultCultureCode).NativeName),
            new("en", CultureInfo.GetCultureInfo("en").NativeName),
            new("de", CultureInfo.GetCultureInfo("de").NativeName),
            new("ru", CultureInfo.GetCultureInfo("ru").NativeName)
        ];

    // 将设置文件中的语言值转换为语言下拉框使用的标准区域标识。
    public static string NormalizeSelectionCulture(string? configuredCulture)
    {
        if (string.IsNullOrWhiteSpace(configuredCulture))
            return AutomaticCultureCode;

        if (configuredCulture.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            return DefaultCultureCode;
        if (configuredCulture.StartsWith("en", StringComparison.OrdinalIgnoreCase))
            return "en";
        if (configuredCulture.StartsWith("de", StringComparison.OrdinalIgnoreCase))
            return "de";
        if (configuredCulture.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
            return "ru";

        return DefaultCultureCode;
    }

    // 将语言选择项转换为设置文件中的稳定语言值，自动项保存为空文化码。
    public static string ToStoredLanguage(LanguageOption option)
        => option.IsAutomatic
            ? AutomaticCultureCode
            : NormalizeSelectionCulture(option.CultureCode);

    // 将用户设置转换为实际使用的文化信息。
    public static CultureInfo ResolveCulture(string? configuredCulture)
    {
        if (!string.IsNullOrWhiteSpace(configuredCulture)
            && TryCreateCulture(configuredCulture, out var configured))
        {
            return configured;
        }

        var systemCulture = CultureInfo.CurrentUICulture;
        if (TryCreateCulture(systemCulture.Name, out var exact))
            return exact;

        if (TryCreateCulture(systemCulture.TwoLetterISOLanguageName, out var parent))
            return parent;

        return CultureInfo.GetCultureInfo(DefaultCultureCode);
    }

    // 应用语言设置，并刷新所有已登记的界面文案。
    public static void ApplyConfiguredCulture(string? configuredCulture)
    {
        var culture = ResolveCulture(configuredCulture);
        TranslationCatalog.SetLanguage(culture.Name);
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = culture;
    }

    // 读取响应式文案当前的字符串值。
    public string GetString(IObservable<string?> observable)
    {
        ArgumentNullException.ThrowIfNull(observable);
        var value = string.Empty;
        using var subscription = observable.Subscribe(new ValueObserver(result => value = result ?? string.Empty));
        return value;
    }

    // 返回一个可在语言切换时自动发出新值的文案对象。
    private IObservable<string?> Text(string key)
    {
        lock (_gate)
        {
            if (!_texts.TryGetValue(key, out var text))
            {
                text = new LocalizedText(key);
                _texts.Add(key, text);
            }

            return text;
        }
    }

    // 向所有已创建的文案对象发布当前语言的新值。
    private void NotifyLanguageChanged()
    {
        LocalizedText[] texts;
        lock (_gate)
            texts = _texts.Values.ToArray();

        foreach (var text in texts)
            text.Publish();
    }

    // 创建一个带有当前语言值的可观察对象。
    public IObservable<string?> Dialog_AcrylicDisabled => Text(nameof(Dialog_AcrylicDisabled));
    public IObservable<string?> Dialog_AcrylicDisabledMsg => Text(nameof(Dialog_AcrylicDisabledMsg));
    public IObservable<string?> Dialog_AcrylicEnabled => Text(nameof(Dialog_AcrylicEnabled));
    public IObservable<string?> Dialog_AcrylicEnabledMsg => Text(nameof(Dialog_AcrylicEnabledMsg));
    public IObservable<string?> Dialog_Cancel => Text(nameof(Dialog_Cancel));
    public IObservable<string?> Dialog_Confirm => Text(nameof(Dialog_Confirm));
    public IObservable<string?> Dialog_ConfirmDelete => Text(nameof(Dialog_ConfirmDelete));
    public IObservable<string?> Dialog_ExportError => Text(nameof(Dialog_ExportError));
    public IObservable<string?> Dialog_ExportSuccess => Text(nameof(Dialog_ExportSuccess));
    public IObservable<string?> Dialog_FeedbackExported => Text(nameof(Dialog_FeedbackExported));
    public IObservable<string?> Dialog_FeedbackMessage => Text(nameof(Dialog_FeedbackMessage));
    public IObservable<string?> Dialog_FeedbackTitle => Text(nameof(Dialog_FeedbackTitle));
    public IObservable<string?> Dialog_GitHubDownload => Text(nameof(Dialog_GitHubDownload));
    public IObservable<string?> Dialog_InputPresetName => Text(nameof(Dialog_InputPresetName));
    public IObservable<string?> Dialog_InvalidName => Text(nameof(Dialog_InvalidName));
    public IObservable<string?> Dialog_InvalidNameTitle => Text(nameof(Dialog_InvalidNameTitle));
    public IObservable<string?> Dialog_MirrorDownload => Text(nameof(Dialog_MirrorDownload));
    public IObservable<string?> Dialog_OK => Text(nameof(Dialog_OK));
    public IObservable<string?> Dialog_RemindLater => Text(nameof(Dialog_RemindLater));
    public IObservable<string?> Dialog_Save => Text(nameof(Dialog_Save));
    public IObservable<string?> Dialog_SkipVersion => Text(nameof(Dialog_SkipVersion));
    public IObservable<string?> Eq_DeleteConfirm => Text(nameof(Eq_DeleteConfirm));
    public IObservable<string?> Eq_HintDeleted => Text(nameof(Eq_HintDeleted));
    public IObservable<string?> Eq_HintEditing => Text(nameof(Eq_HintEditing));
    public IObservable<string?> Eq_HintNewPreset => Text(nameof(Eq_HintNewPreset));
    public IObservable<string?> Eq_HintReset => Text(nameof(Eq_HintReset));
    public IObservable<string?> Eq_HintSaved => Text(nameof(Eq_HintSaved));
    public IObservable<string?> Eq_HintSwitched => Text(nameof(Eq_HintSwitched));
    public IObservable<string?> Feature_FindDevice => Text(nameof(Feature_FindDevice));
    public IObservable<string?> Feature_StopFindDevice => Text(nameof(Feature_StopFindDevice));
    public IObservable<string?> ImagePicker_FilterName => Text(nameof(ImagePicker_FilterName));
    public IObservable<string?> ImagePicker_Title => Text(nameof(ImagePicker_Title));
    public IObservable<string?> Log_ExportTitle => Text(nameof(Log_ExportTitle));
    public IObservable<string?> Log_ExportZip => Text(nameof(Log_ExportZip));
    public IObservable<string?> MultiDevice_AllHidden => Text(nameof(MultiDevice_AllHidden));
    public IObservable<string?> MultiDevice_Automatic => Text(nameof(MultiDevice_Automatic));
    public IObservable<string?> MultiDevice_Connect => Text(nameof(MultiDevice_Connect));
    public IObservable<string?> MultiDevice_Disconnect => Text(nameof(MultiDevice_Disconnect));
    public IObservable<string?> MultiDevice_EmptyHint => Text(nameof(MultiDevice_EmptyHint));
    public IObservable<string?> MultiDevice_Hide => Text(nameof(MultiDevice_Hide));
    public IObservable<string?> MultiDevice_NoOtherDevices => Text(nameof(MultiDevice_NoOtherDevices));
    public IObservable<string?> MultiDevice_PriorityHint => Text(nameof(MultiDevice_PriorityHint));
    public IObservable<string?> MultiDevice_PriorityUnavailable => Text(nameof(MultiDevice_PriorityUnavailable));
    public IObservable<string?> MultiDevice_RestoreHidden => Text(nameof(MultiDevice_RestoreHidden));
    public IObservable<string?> MultiDevice_StatusConnected => Text(nameof(MultiDevice_StatusConnected));
    public IObservable<string?> MultiDevice_StatusConnecting => Text(nameof(MultiDevice_StatusConnecting));
    public IObservable<string?> MultiDevice_StatusCurrentDevice => Text(nameof(MultiDevice_StatusCurrentDevice));
    public IObservable<string?> MultiDevice_StatusDisconnected => Text(nameof(MultiDevice_StatusDisconnected));
    public IObservable<string?> MultiDevice_Unpair => Text(nameof(MultiDevice_Unpair));
    public IObservable<string?> Personal_Custom => Text(nameof(Personal_Custom));
    public IObservable<string?> Personal_EarphoneCase => Text(nameof(Personal_EarphoneCase));
    public IObservable<string?> Personal_EarphoneDual => Text(nameof(Personal_EarphoneDual));
    public IObservable<string?> Personal_EarphoneLeft => Text(nameof(Personal_EarphoneLeft));
    public IObservable<string?> Personal_EarphoneRight => Text(nameof(Personal_EarphoneRight));
    public IObservable<string?> Personal_LanguageAuto => Text(nameof(Personal_LanguageAuto));
    public IObservable<string?> Settings_AllModels => Text(nameof(Settings_AllModels));
    public IObservable<string?> Settings_AllSeries => Text(nameof(Settings_AllSeries));
    public IObservable<string?> Settings_AutoDetect => Text(nameof(Settings_AutoDetect));
    public IObservable<string?> Settings_Checking => Text(nameof(Settings_Checking));
    public IObservable<string?> Settings_CheckUpdate => Text(nameof(Settings_CheckUpdate));
    public IObservable<string?> Settings_ModelAutoDetected => Text(nameof(Settings_ModelAutoDetected));
    public IObservable<string?> Settings_ModelManualSet => Text(nameof(Settings_ModelManualSet));
    public IObservable<string?> Settings_RestoreHiddenDevices => Text(nameof(Settings_RestoreHiddenDevices));
    public IObservable<string?> SpatialAudio_ModeFixed => Text(nameof(SpatialAudio_ModeFixed));
    public IObservable<string?> SpatialAudio_ModeHeadTrack => Text(nameof(SpatialAudio_ModeHeadTrack));
    public IObservable<string?> SpatialAudio_ModeOff => Text(nameof(SpatialAudio_ModeOff));
    public IObservable<string?> Status_Connected => Text(nameof(Status_Connected));
    public IObservable<string?> Status_Disconnected => Text(nameof(Status_Disconnected));
    public IObservable<string?> Status_Identifying => Text(nameof(Status_Identifying));
    public IObservable<string?> Status_Unidentified => Text(nameof(Status_Unidentified));
    public IObservable<string?> Toast_NewVersion => Text(nameof(Toast_NewVersion));
    public IObservable<string?> Toast_VersionLabel => Text(nameof(Toast_VersionLabel));
    public IObservable<string?> Update_ConnectFailed => Text(nameof(Update_ConnectFailed));
    public IObservable<string?> Update_MessageNoContent => Text(nameof(Update_MessageNoContent));
    public IObservable<string?> Update_MessageWithContent => Text(nameof(Update_MessageWithContent));
    public IObservable<string?> Update_NetworkError => Text(nameof(Update_NetworkError));
    public IObservable<string?> Update_ParseError => Text(nameof(Update_ParseError));
    public IObservable<string?> Update_Timeout => Text(nameof(Update_Timeout));
    public IObservable<string?> Update_UpToDate => Text(nameof(Update_UpToDate));

    private static bool TryCreateCulture(string name, out CultureInfo culture)
    {
        try
        {
            culture = CultureInfo.GetCultureInfo(name);
            return true;
        }
        catch (CultureNotFoundException)
        {
            culture = CultureInfo.InvariantCulture;
            return false;
        }
    }

    // 保存一个本地化键，并向订阅者推送语言切换后的值。
    private sealed class LocalizedText : IObservable<string?>
    {
        private readonly object _gate = new();
        private readonly List<IObserver<string?>> _observers = [];

        public LocalizedText(string key)
        {
            Key = key;
        }

        private string Key { get; }

        public IDisposable Subscribe(IObserver<string?> observer)
        {
            ArgumentNullException.ThrowIfNull(observer);
            lock (_gate)
                _observers.Add(observer);
            observer.OnNext(TranslationCatalog.Get(Key));
            return new ObserverSubscription(this, observer);
        }

        public void Publish()
        {
            IObserver<string?>[] observers;
            lock (_gate)
                observers = _observers.ToArray();

            var value = TranslationCatalog.Get(Key);
            foreach (var observer in observers)
                observer.OnNext(value);
        }

        private void Remove(IObserver<string?> observer)
        {
            lock (_gate)
                _observers.Remove(observer);
        }

        private sealed class ObserverSubscription(LocalizedText owner, IObserver<string?> observer) : IDisposable
        {
            public void Dispose() => owner.Remove(observer);
        }
    }

    // 把一次性读取转换成 IObserver，避免引入额外的响应式运行时依赖。
    private sealed class ValueObserver(Action<string?> callback) : IObserver<string?>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(string? value) => callback(value);
    }
}

// 提供最小的响应式订阅扩展，避免为简单的语言通知引入额外依赖。
public static class ObservableExtensions
{
    // 使用委托订阅可观察值，并返回可释放的订阅句柄。
    public static IDisposable Subscribe<T>(this IObservable<T> source, Action<T> onNext)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(onNext);
        return source.Subscribe(new DelegateObserver<T>(onNext));
    }

    // 将委托包装成标准的可观察对象观察者。
    private sealed class DelegateObserver<T>(Action<T> onNext) : IObserver<T>
    {
        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void OnNext(T value) => onNext(value);
    }
}
