namespace ClassSoftwareHub.Desktop.Services;

/// <summary>
/// 遥测接口（预留）。当前只有 Noop 实现：不联网、不上报。
/// 以后接 Umami / Sentry / 自建 HTTPS 接口时，实现这个接口替换即可，其余代码不用改。
/// </summary>
public interface ITelemetryService
{
    /// <summary>是否已开启上报（默认 false）。</summary>
    bool Enabled { get; }

    /// <summary>记录一个匿名事件（启动、崩溃、工具使用等）。失败必须静默。</summary>
    void Track(string eventName, IReadOnlyDictionary<string, object?>? properties = null);

    void TrackException(Exception ex, string? context = null);
}

/// <summary>空实现：满足接口，什么都不做。</summary>
public sealed class NoopTelemetryService : ITelemetryService
{
    private readonly SettingsStore _settings;

    public NoopTelemetryService(SettingsStore settings) => _settings = settings;

    public bool Enabled => _settings.Current.TelemetryEnabled;

    public void Track(string eventName, IReadOnlyDictionary<string, object?>? properties = null)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[telemetry:noop] {eventName}");
#endif
    }

    public void TrackException(Exception ex, string? context = null)
    {
#if DEBUG
        System.Diagnostics.Debug.WriteLine($"[telemetry:noop] exception {context}: {ex.Message}");
#endif
    }
}
