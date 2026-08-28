using HidSharp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed class AjazzClockSyncService(ILogger<AjazzClockSyncService> logger, IAjazzSettingsStore settingsStore) : BackgroundService
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Lock _devicePathsLock = new();
    private HashSet<string> _knownAjazzDevicePaths = [];
    private DeviceList? _deviceList;
    private CancellationToken _stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        logger.LogInformation("AJAZZ Clock Sync started.");

        _deviceList = DeviceList.Local;
        lock (_devicePathsLock)
        {
            _knownAjazzDevicePaths = GetCurrentAjazzDevicePaths();
        }

        _deviceList.Changed += OnDeviceListChanged;

        if (settingsStore.GetSettings().SyncOnStartup)
        {
            await TrySyncNowAsync("startup", stoppingToken);
        }
        else
        {
            logger.LogInformation("Startup sync is disabled by configuration.");
        }

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                AjazzSettings settings = settingsStore.GetSettings();

                if (!settings.SyncIntervalEnabled)
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                    continue;
                }

                TimeSpan delay = TimeSpan.FromHours(settings.SyncIntervalHours);
                await Task.Delay(delay, stoppingToken);
                await TrySyncNowAsync("scheduled", stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            if (_deviceList is not null)
            {
                _deviceList.Changed -= OnDeviceListChanged;
            }

            logger.LogInformation("AJAZZ Clock Sync stopped.");
        }
    }

    public IReadOnlyList<AjazzHidDeviceInfo> GetAjazzDevices()
    {
        return DeviceList.Local
            .GetHidDevices()
            .Where(IsAjazzTimeInterface)
            .Select(device => new AjazzHidDeviceInfo(
                device.DevicePath,
                device.GetFriendlyName() ?? string.Empty,
                device.VendorID,
                device.ProductID))
            .OrderBy(device => device.ProductName)
            .ToList();
    }

    public Task<bool> TrySyncNowAsync(string reason, CancellationToken cancellationToken = default)
    {
        return TrySyncAsync(reason, DateTime.Now, cancellationToken);
    }

    public Task<bool> TrySyncAtAsync(string reason, DateTime targetDateTime, CancellationToken cancellationToken = default)
    {
        return TrySyncAsync(reason, targetDateTime, cancellationToken);
    }

    private void OnDeviceListChanged(object? sender, DeviceListChangedEventArgs e)
    {
        if (_stoppingToken.IsCancellationRequested)
        {
            return;
        }

        logger.LogInformation("HID device list changed (plug/unplug). Evaluating AJAZZ connect state.");

        bool hasNewConnection = false;
        lock (_devicePathsLock)
        {
            var current = GetCurrentAjazzDevicePaths();
            hasNewConnection = current.Except(_knownAjazzDevicePaths, StringComparer.OrdinalIgnoreCase).Any();
            _knownAjazzDevicePaths = current;
        }

        if (!hasNewConnection)
        {
            return;
        }

        if (!settingsStore.GetSettings().SyncOnDeviceConnect)
        {
            logger.LogInformation("Device-connect sync is disabled by configuration.");
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(1500, _stoppingToken);
                await TrySyncNowAsync("device connect", _stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // normal shutdown
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error while handling device change.");
            }
        }, _stoppingToken);
    }

    private async Task<bool> TrySyncAsync(string reason, DateTime targetDateTime, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            AjazzSettings settings = settingsStore.GetSettings();
            IEnumerable<HidDevice> candidates = GetSyncCandidates(settings);

            foreach (var device in candidates)
            {
                try
                {
                    string productName = device.GetFriendlyName() ?? string.Empty;

                    logger.LogInformation("Found device ({Reason}): {ProductName} {DevicePath}", reason, productName, device.DevicePath);

                    if (!device.TryOpen(out HidStream? stream))
                    {
                        logger.LogWarning("Unable to open AJAZZ HID interface.");
                        continue;
                    }

                    using (stream)
                    {
                        byte[] payload = BuildTimePacket(targetDateTime);

                        logger.LogInformation("Syncing clock: {Timestamp:yyyy-MM-dd HH:mm:ss}", targetDateTime);
                        stream.SetFeature(payload);
                        logger.LogInformation("Clock sync succeeded.");
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "A matching interface rejected the time sync packet.");
                }
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedDevicePath))
            {
                logger.LogInformation("No compatible AJAZZ interface accepted the time sync packet.");
            }
            else
            {
                logger.LogInformation("Selected AJAZZ device was not available or did not accept the sync packet.");
            }

            return false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static IEnumerable<HidDevice> GetAllAjazzTimeInterfaces()
    {
        return DeviceList.Local.GetHidDevices().Where(IsAjazzTimeInterface);
    }

    private static HashSet<string> GetCurrentAjazzDevicePaths()
    {
        return GetAllAjazzTimeInterfaces()
            .Select(d => d.DevicePath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<HidDevice> GetSyncCandidates(AjazzSettings settings)
    {
        var all = GetAllAjazzTimeInterfaces();

        if (string.IsNullOrWhiteSpace(settings.SelectedDevicePath))
        {
            return all;
        }

        string selected = settings.SelectedDevicePath.Trim();
        var selectedDevice = all.Where(d => string.Equals(d.DevicePath, selected, StringComparison.OrdinalIgnoreCase));
        return selectedDevice;
    }

    private static bool IsAjazzTimeInterface(HidDevice device)
    {
        try
        {
            string productName = device.GetFriendlyName() ?? string.Empty;
            return productName.Contains("AJAZZ", StringComparison.OrdinalIgnoreCase)
                && productName.Contains("2.4G", StringComparison.OrdinalIgnoreCase)
                && device.DevicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] BuildTimePacket(DateTime now)
    {
        byte[] payload = new byte[65];

        payload[1] = 0x28;
        payload[8] = 0xD7;

        payload[9] = (byte)(now.Year >> 8);
        payload[10] = (byte)(now.Year & 0xFF);

        payload[11] = (byte)now.Month;
        payload[12] = (byte)now.Day;
        payload[13] = (byte)now.Hour;
        payload[14] = (byte)now.Minute;
        payload[15] = (byte)now.Second;

        return payload;
    }
}
