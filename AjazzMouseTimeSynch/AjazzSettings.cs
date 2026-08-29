using System.Text.Json;

public sealed class AjazzSettings
{
    public string WebHost { get; set; } = "http://127.0.0.1:5580";
    public string SelectedDevicePath { get; set; } = string.Empty;
    public int SyncIntervalHours { get; set; } = 1;
    public int BatteryPollIntervalSeconds { get; set; } = 60;
    public bool SyncIntervalEnabled { get; set; } = true;
    public bool SyncOnStartup { get; set; } = true;
    public bool SyncOnDeviceConnect { get; set; } = true;
    public string LastCustomDateTime { get; set; } = "9999-09-09T00:00";
}

public sealed class AjazzSettingsUpdateRequest
{
    public string? SelectedDevicePath { get; set; }
    public int? SyncIntervalHours { get; set; }
    public int? BatteryPollIntervalSeconds { get; set; }
    public bool? SyncIntervalEnabled { get; set; }
    public bool? SyncOnStartup { get; set; }
    public bool? SyncOnDeviceConnect { get; set; }
    public string? LastCustomDateTime { get; set; }
}

public sealed class AjazzManualSyncRequest
{
    public DateTime? TargetDateTime { get; set; }
}

public sealed record AjazzHidDeviceInfo(string DevicePath, string ProductName, int VendorId, int ProductId);

public sealed class AjazzCaptureRequest
{
    public string Label { get; set; } = string.Empty;
}

public sealed class AjazzCaptureDiffRequest
{
    public string LeftLabel { get; set; } = string.Empty;
    public string RightLabel { get; set; } = string.Empty;
    public int ReportId { get; set; }
    public int InterfaceNumber { get; set; }
    public int Endpoint { get; set; }
}

public interface IAjazzSettingsStore
{
    AjazzSettings GetSettings();
    AjazzSettings UpdateSettings(AjazzSettingsUpdateRequest update);
}

public sealed class AjazzSettingsStore(IConfiguration configuration, IHostEnvironment hostEnvironment, ILogger<AjazzSettingsStore> logger) : IAjazzSettingsStore
{
    private static readonly HashSet<int> AllowedBatteryPollIntervalsSeconds = [5, 10, 15, 30, 45, 60, 120, 180, 300, 600, 900];

    private readonly Lock _lock = new();
    private AjazzSettings _settings = Load(configuration);

    public AjazzSettings GetSettings()
    {
        lock (_lock)
        {
            return Clone(_settings);
        }
    }

    public AjazzSettings UpdateSettings(AjazzSettingsUpdateRequest update)
    {
        lock (_lock)
        {
            if (update.SelectedDevicePath is not null)
            {
                _settings.SelectedDevicePath = update.SelectedDevicePath.Trim();
            }

            if (update.SyncIntervalHours.HasValue)
            {
                _settings.SyncIntervalHours = NormalizeInterval(update.SyncIntervalHours.Value);
            }

            if (update.BatteryPollIntervalSeconds.HasValue)
            {
                _settings.BatteryPollIntervalSeconds = NormalizeBatteryPollInterval(update.BatteryPollIntervalSeconds.Value);
            }

            if (update.SyncIntervalEnabled.HasValue)
            {
                _settings.SyncIntervalEnabled = update.SyncIntervalEnabled.Value;
            }

            if (update.SyncOnStartup.HasValue)
            {
                _settings.SyncOnStartup = update.SyncOnStartup.Value;
            }

            if (update.SyncOnDeviceConnect.HasValue)
            {
                _settings.SyncOnDeviceConnect = update.SyncOnDeviceConnect.Value;
            }

            if (update.LastCustomDateTime is not null)
            {
                _settings.LastCustomDateTime = NormalizeCustomDateTime(update.LastCustomDateTime);
            }

            Persist(_settings);
            return Clone(_settings);
        }
    }

    private static AjazzSettings Load(IConfiguration configuration)
    {
        var settings = new AjazzSettings();
        configuration.GetSection("Ajazz").Bind(settings);

        settings.WebHost = NormalizeWebHost(settings.WebHost);
        settings.SyncIntervalHours = NormalizeInterval(settings.SyncIntervalHours);
        settings.BatteryPollIntervalSeconds = NormalizeBatteryPollInterval(settings.BatteryPollIntervalSeconds);
        settings.SelectedDevicePath ??= string.Empty;
        settings.LastCustomDateTime = NormalizeCustomDateTime(settings.LastCustomDateTime);

        return settings;
    }

    private static AjazzSettings Clone(AjazzSettings settings)
    {
        return new AjazzSettings
        {
            WebHost = settings.WebHost,
            SelectedDevicePath = settings.SelectedDevicePath,
            SyncIntervalHours = settings.SyncIntervalHours,
            BatteryPollIntervalSeconds = settings.BatteryPollIntervalSeconds,
            SyncIntervalEnabled = settings.SyncIntervalEnabled,
            SyncOnStartup = settings.SyncOnStartup,
            SyncOnDeviceConnect = settings.SyncOnDeviceConnect,
            LastCustomDateTime = settings.LastCustomDateTime
        };
    }

    private static int NormalizeInterval(int intervalHours)
    {
        return intervalHours < 1 ? 1 : intervalHours;
    }

    private static int NormalizeBatteryPollInterval(int intervalSeconds)
    {
        return AllowedBatteryPollIntervalsSeconds.Contains(intervalSeconds) ? intervalSeconds : 60;
    }

    private static string NormalizeWebHost(string? webHost)
    {
        if (string.IsNullOrWhiteSpace(webHost))
        {
            return "http://127.0.0.1:5580";
        }

        return webHost.Trim();
    }

    private static string NormalizeCustomDateTime(string? customDateTime)
    {
        if (string.IsNullOrWhiteSpace(customDateTime))
        {
            return "9999-09-09T00:00";
        }

        return customDateTime.Trim();
    }

    private void Persist(AjazzSettings settings)
    {
        string appSettingsPath = Path.Combine(hostEnvironment.ContentRootPath, "appsettings.json");

        var root = File.Exists(appSettingsPath)
            ? JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(appSettingsPath))
            : null;

        var normalized = root is null
            ? new Dictionary<string, object?>()
            : root.ToDictionary(k => k.Key, v => JsonSerializer.Deserialize<object?>(v.Value.GetRawText()));

        normalized["Ajazz"] = new AjazzSettings
        {
            WebHost = settings.WebHost,
            SelectedDevicePath = settings.SelectedDevicePath,
            SyncIntervalHours = settings.SyncIntervalHours,
            BatteryPollIntervalSeconds = settings.BatteryPollIntervalSeconds,
            SyncIntervalEnabled = settings.SyncIntervalEnabled,
            SyncOnStartup = settings.SyncOnStartup,
            SyncOnDeviceConnect = settings.SyncOnDeviceConnect,
            LastCustomDateTime = settings.LastCustomDateTime
        };

        string json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(appSettingsPath, json);
        logger.LogInformation("Saved AJAZZ settings to appsettings.json.");
    }
}
