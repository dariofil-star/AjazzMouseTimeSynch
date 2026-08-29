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
    public AjazzDebugLoggingSettings DebugLogging { get; set; } = new();
}

public sealed class AjazzDebugLoggingSettings
{
    public bool Enabled { get; set; }
    public string LogFilePath { get; set; } = "logs/ajazz-hid-usb.log";
    public string VendorId { get; set; } = "0x3151";
    public string[] ProductIds { get; set; } = ["0x5007", "0x4026"];
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

// ── Firmware version ───────────────────────────────────────────────────────────

public sealed record AjazzFirmwareVersion(ushort Raw, string Text);

// ── LED setting (0x07 SET_LEDPARAM / 0x87 GET_LEDPARAM, also nested in 0x53) ─

public sealed class AjazzLedSetting
{
    /// <summary>0=Off 1=AlwaysOn 2=Breath 3=Neon 4=Wave 5=Dazzing 6=Laser 7=MusicFollow 8=ScreenColor 9=MusicFollow2 10=UserPicture</summary>
    public int EffectType { get; set; }
    /// <summary>UI speed 0-4.  Wire byte = 4 - Speed.</summary>
    public int Speed { get; set; }
    /// <summary>Brightness 0-6 (carried in byte 3 of the 8-byte block).</summary>
    public int Brightness { get; set; }
    /// <summary>Dazzle modifier toggle.</summary>
    public bool Dazzle { get; set; }
    /// <summary>Effect-specific option (wave direction, music mode…).</summary>
    public int Option { get; set; }
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
}

// ── DPI table (0x54 SET_OPTIONPARAM1 / 0xD4 GET_OPTIONPARAM1) ─────────────────

public sealed class AjazzDpiStageColor
{
    public byte R { get; set; }
    public byte G { get; set; }
    public byte B { get; set; }
}

public sealed class AjazzDpiTableRequest
{
    public int ProfileIndex { get; set; }
    /// <summary>Currently active stage index (0-7).</summary>
    public int ActiveStage { get; set; }
    /// <summary>Number of enabled stages (0-8).  Values for disabled stages are ignored.</summary>
    public int StageCount { get; set; }
    /// <summary>DPI values for each of the 8 stages.</summary>
    public int[] DpiValues { get; set; } = new int[8];
    /// <summary>Per-stage indicator LED colours (max 7 writable; stage-8 B-channel is overwritten by checksum on wire).</summary>
    public AjazzDpiStageColor[] Colors { get; set; } = Enumerable.Range(0, 8).Select(_ => new AjazzDpiStageColor()).ToArray();
}

// ── Button rebind (0x50 SET_KEYMATRIX) ────────────────────────────────────────

public sealed class AjazzButtonBindRequest
{
    public int ProfileIndex { get; set; }
    /// <summary>Physical button index (0-based, resolved via the device's default matrix).</summary>
    public int ButtonIndex { get; set; }
    /// <summary>4-byte changeArr: [type, byte1, byte2, byte3].  See protocol docs for action encoding.</summary>
    public byte[] ActionBytes { get; set; } = new byte[4];
}

// ── Profile (0x05 SET_PROFILE / 0x85 GET_PROFILE) ────────────────────────────

public sealed class AjazzSetProfileRequest
{
    /// <summary>Profile index 0-7.</summary>
    public int ProfileIndex { get; set; }
}

// ── Sleep times (nested in 0x53) ──────────────────────────────────────────────

public sealed class AjazzSleepTimes
{
    public ushort IdleBtSeconds { get; set; }
    public ushort DeepBtSeconds { get; set; }
    public ushort Idle24gSeconds { get; set; }
    public ushort Deep24gSeconds { get; set; }
}

// ── Battery LED colours (nested in 0x53) ─────────────────────────────────────

public sealed class AjazzBatteryLedColors
{
    public byte HighR { get; set; }
    public byte HighG { get; set; }
    public byte HighB { get; set; }
    public byte LowR { get; set; }
    public byte LowG { get; set; }
    public byte LowB { get; set; }
}

// ── Omnibus mouse settings (0x53 SET_OPTIONPARAM0 / 0xD3 GET_OPTIONPARAM0) ───

public sealed class AjazzMouseSettingsRequest
{
    public int ProfileIndex { get; set; }
    /// <summary>Polling-rate wire code from _RateToNum: 125Hz=0x08 250Hz=0x04 500Hz=0x02 1000Hz=0x01 2000Hz=0x84 4000Hz=0x82 8000Hz=0x81.</summary>
    public int PollingRateCode { get; set; } = 0x01;
    /// <summary>Debounce time in ms (0-10 typical).</summary>
    public int DebounceMs { get; set; }
    public bool LightOff { get; set; }
    public bool WheelLightOff { get; set; }
    public bool MotionSmoothing { get; set; }
    public int WheelToButton { get; set; } = 10;
    public int ButtonToWheel { get; set; } = 10;
    public AjazzLedSetting Light { get; set; } = new();
    public AjazzLedSetting LogoLight { get; set; } = new();
    public AjazzSleepTimes Sleep { get; set; } = new();
    /// <summary>X-axis sensitivity multiplier 0-100 (default 100).</summary>
    public int XSensitivity { get; set; } = 100;
    /// <summary>Y-axis sensitivity multiplier 0-100 (default 100).</summary>
    public int YSensitivity { get; set; } = 100;
    /// <summary>Lift-off distance: 0=1mm 1=2mm 2=3mm.</summary>
    public int LiftCutOff { get; set; }
    public bool AngleSnap { get; set; }
    public AjazzBatteryLedColors BatteryColors { get; set; } = new();
    /// <summary>Whether to illuminate the LED while charging.</summary>
    public bool ChargingSwitch { get; set; }
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
        settings.DebugLogging = NormalizeDebugLogging(settings.DebugLogging);

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
            LastCustomDateTime = settings.LastCustomDateTime,
            DebugLogging = new AjazzDebugLoggingSettings
            {
                Enabled = settings.DebugLogging.Enabled,
                LogFilePath = settings.DebugLogging.LogFilePath,
                VendorId = settings.DebugLogging.VendorId,
                ProductIds = settings.DebugLogging.ProductIds?.ToArray() ?? []
            }
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

    private static AjazzDebugLoggingSettings NormalizeDebugLogging(AjazzDebugLoggingSettings? debugLogging)
    {
        var normalized = debugLogging ?? new AjazzDebugLoggingSettings();

        normalized.LogFilePath = string.IsNullOrWhiteSpace(normalized.LogFilePath)
            ? "logs/ajazz-hid-usb.log"
            : normalized.LogFilePath.Trim();

        normalized.VendorId = string.IsNullOrWhiteSpace(normalized.VendorId)
            ? "0x3151"
            : normalized.VendorId.Trim();

        normalized.ProductIds = normalized.ProductIds?
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray()
            ?? ["0x5007", "0x4026"];

        return normalized;
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
            LastCustomDateTime = settings.LastCustomDateTime,
            DebugLogging = settings.DebugLogging
        };

        string json = JsonSerializer.Serialize(normalized, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(appSettingsPath, json);
        logger.LogInformation("Saved AJAZZ settings to appsettings.json.");
    }
}
