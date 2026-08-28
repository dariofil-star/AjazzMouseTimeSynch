using System.Text.Json;

public sealed class AjazzSettings
{
    public int WebPort { get; set; } = 5088;
    public string SelectedDevicePath { get; set; } = string.Empty;
    public int SyncIntervalHours { get; set; } = 1;
    public bool SyncIntervalEnabled { get; set; } = true;
    public bool SyncOnStartup { get; set; } = true;
    public bool SyncOnDeviceConnect { get; set; } = true;
    public string LastCustomDateTime { get; set; } = "9999-09-09T00:00";
}

public sealed class AjazzSettingsUpdateRequest
{
    public string? SelectedDevicePath { get; set; }
    public int? SyncIntervalHours { get; set; }
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

public interface IAjazzSettingsStore
{
    AjazzSettings GetSettings();
    AjazzSettings UpdateSettings(AjazzSettingsUpdateRequest update);
}

public sealed class AjazzSettingsStore(IConfiguration configuration, IHostEnvironment hostEnvironment, ILogger<AjazzSettingsStore> logger) : IAjazzSettingsStore
{
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

        settings.WebPort = settings.WebPort <= 0 ? 5088 : settings.WebPort;
        settings.SyncIntervalHours = NormalizeInterval(settings.SyncIntervalHours);
        settings.SelectedDevicePath ??= string.Empty;
        settings.LastCustomDateTime = NormalizeCustomDateTime(settings.LastCustomDateTime);

        return settings;
    }

    private static AjazzSettings Clone(AjazzSettings settings)
    {
        return new AjazzSettings
        {
            WebPort = settings.WebPort,
            SelectedDevicePath = settings.SelectedDevicePath,
            SyncIntervalHours = settings.SyncIntervalHours,
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
            WebPort = settings.WebPort,
            SelectedDevicePath = settings.SelectedDevicePath,
            SyncIntervalHours = settings.SyncIntervalHours,
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
