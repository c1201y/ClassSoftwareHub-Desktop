namespace ClassSoftwareHub.Desktop.Services;





public interface ITelemetryService
{
    
    bool Enabled { get; }

    
    void Track(string eventName, IReadOnlyDictionary<string, object?>? properties = null);

    void TrackException(Exception ex, string? context = null);
}


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
