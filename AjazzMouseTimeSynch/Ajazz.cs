using System.Runtime.InteropServices;
using HidSharp;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

public sealed record AjazzMonitoringStatus(
    int? BatteryPercentage,
    string PowerCaptureState,
    string ActivityCaptureState,
    DateTimeOffset? LastBatteryReadUtc,
    DateTimeOffset? LastActivityReadUtc,
    string Transport,
    string DevicePath,
    bool IsConnected,
    string ConnectionMode,
    string DeviceInstanceId,
    string Manufacturer,
    string Product,
    string LastConnectionTransition);

public sealed class AjazzClockSyncService(ILogger<AjazzClockSyncService> logger, IAjazzSettingsStore settingsStore) : BackgroundService
{
    private static readonly EventId ServiceStartedEvent = new(1000, nameof(ServiceStartedEvent));
    private static readonly EventId ServiceStoppedEvent = new(1001, nameof(ServiceStoppedEvent));
    private static readonly EventId TimeSyncUpdatedEvent = new(1100, nameof(TimeSyncUpdatedEvent));
    private static readonly EventId DeviceChangeErrorEvent = new(1200, nameof(DeviceChangeErrorEvent));
    private static readonly EventId TimeSyncErrorEvent = new(1201, nameof(TimeSyncErrorEvent));

    private static readonly HashSet<string> PowerCaptureStates = new(StringComparer.Ordinal)
    {
        "awake-off-dock",
        "idle-off-dock",
        "sleeping-off-dock",
        "placed-on-dock",
        "charging-on-dock",
        "disconnected",
        "usb-cable-connected",
        "usb-cable-charging",
        "fully-charged-on-dock",
        "fully-charged-on-usb"
    };

    private static readonly HashSet<string> ActivityCaptureStates = new(StringComparer.Ordinal)
    {
        "awake-and-moving",
        "idle-but-awake",
        "actual-sleep",
        "wake-after-movement",
        "disconnected"
    };

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Lock _monitoringLock = new();
    private readonly AjazzDeviceMonitor _deviceMonitor = new(TimeSpan.FromMilliseconds(750));

    private CancellationToken _stoppingToken;
    private AjazzDeviceSnapshot _currentSnapshot = AjazzDeviceSnapshot.Disconnected;

    private AjazzMonitoringStatus _monitoringStatus = new(
        BatteryPercentage: null,
        PowerCaptureState: "disconnected",
        ActivityCaptureState: "disconnected",
        LastBatteryReadUtc: null,
        LastActivityReadUtc: null,
        Transport: "disconnected",
        DevicePath: string.Empty,
        IsConnected: false,
        ConnectionMode: "disconnected",
        DeviceInstanceId: string.Empty,
        Manufacturer: string.Empty,
        Product: string.Empty,
        LastConnectionTransition: string.Empty);

    private DateTimeOffset _lastSystemInputUtc = DateTimeOffset.MinValue;
    private DateTimeOffset? _wakeAfterMovementUntilUtc;
    private int? _lastBatteryPercentage;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        logger.LogWarning(ServiceStartedEvent, "AJAZZ Clock Sync service started.");

        _deviceMonitor.SnapshotChanged += OnDeviceSnapshotChanged;
        _deviceMonitor.Start();

        if (settingsStore.GetSettings().SyncOnStartup)
        {
            await TrySyncNowAsync("startup", stoppingToken);
        }
        else
        {
            logger.LogDebug("Startup sync is disabled by configuration.");
        }

        Task clockSyncTask = RunScheduledClockSyncLoopAsync(stoppingToken);
        Task batteryTask = RunBatteryPollingLoopAsync(stoppingToken);
        Task activityTask = RunActivityStateLoopAsync(stoppingToken);

        try
        {
            await Task.WhenAll(clockSyncTask, batteryTask, activityTask);
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        finally
        {
            _deviceMonitor.SnapshotChanged -= OnDeviceSnapshotChanged;
            _deviceMonitor.Dispose();
            logger.LogWarning(ServiceStoppedEvent, "AJAZZ Clock Sync service stopped.");
        }
    }

    public IReadOnlyList<AjazzHidDeviceInfo> GetAjazzDevices()
    {
        return DeviceList.Local
            .GetHidDevices()
            .Where(IsAjazzControlInterface)
            .Select(device => new AjazzHidDeviceInfo(
                device.DevicePath,
                device.GetFriendlyName() ?? string.Empty,
                device.VendorID,
                device.ProductID))
            .OrderBy(device => device.ProductName)
            .ToList();
    }

    public AjazzMonitoringStatus GetMonitoringStatus()
    {
        lock (_monitoringLock)
        {
            return _monitoringStatus with
            {
                PowerCaptureState = FormatPowerState(_monitoringStatus.PowerCaptureState),
                ActivityCaptureState = FormatActivityState(_monitoringStatus.ActivityCaptureState),
                Transport = FormatTransport(_monitoringStatus.Transport),
                ConnectionMode = FormatConnectionMode(_monitoringStatus.ConnectionMode),
                LastConnectionTransition = FormatConnectionTransition(_monitoringStatus.LastConnectionTransition)
            };
        }
    }

    public Task<bool> TrySyncNowAsync(string reason, CancellationToken cancellationToken = default)
    {
        return TrySyncAsync(reason, DateTime.Now, cancellationToken);
    }

    public Task<bool> TrySyncAtAsync(string reason, DateTime targetDateTime, CancellationToken cancellationToken = default)
    {
        return TrySyncAsync(reason, targetDateTime, cancellationToken);
    }

    private async Task RunScheduledClockSyncLoopAsync(CancellationToken stoppingToken)
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

    private async Task RunBatteryPollingLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollBatteryOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(DeviceChangeErrorEvent, ex, "Battery polling loop failed for one cycle.");
            }

            int seconds = settingsStore.GetSettings().BatteryPollIntervalSeconds;
            await Task.Delay(TimeSpan.FromSeconds(seconds), stoppingToken);
        }
    }

    private async Task RunActivityStateLoopAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                RefreshActivityStateFromSystemInput(DateTimeOffset.UtcNow);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Unable to refresh activity state from Windows last-input information.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }
    }

    private async Task PollBatteryOnceAsync(CancellationToken cancellationToken)
    {
        AjazzSettings settings = settingsStore.GetSettings();
        List<HidDevice> candidates = GetSyncCandidates(settings, _currentSnapshot).ToList();

        foreach (HidDevice device in candidates)
        {
            try
            {
                if (!device.TryOpen(out HidStream? stream))
                {
                    continue;
                }

                using (stream)
                {
                    int? battery = await TryReadBatteryPercentageAsync(stream, cancellationToken);
                    if (!battery.HasValue)
                    {
                        continue;
                    }

                    DateTimeOffset now = DateTimeOffset.UtcNow;
                    AjazzDeviceSnapshot snapshot = _currentSnapshot;
                    string transport = GetTransportKey(device, snapshot);
                    string activityState;
                    string powerState;

                    lock (_monitoringLock)
                    {
                        activityState = _monitoringStatus.ActivityCaptureState;
                        powerState = ResolvePowerState(snapshot.Mode, activityState, battery, _lastBatteryPercentage, connected: true);
                        _lastBatteryPercentage = battery;
                        _monitoringStatus = _monitoringStatus with
                        {
                            BatteryPercentage = battery,
                            LastBatteryReadUtc = now,
                            Transport = transport,
                            DevicePath = device.DevicePath,
                            IsConnected = true,
                            ConnectionMode = GetConnectionModeKey(snapshot.Mode),
                            DeviceInstanceId = snapshot.DeviceInstanceId,
                            Manufacturer = snapshot.Manufacturer,
                            Product = snapshot.Product,
                            PowerCaptureState = PowerCaptureStates.Contains(powerState) ? powerState : _monitoringStatus.PowerCaptureState
                        };
                    }

                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to read battery from candidate HID interface.");
            }
        }

        if (_currentSnapshot.Mode == AjazzConnectionMode.Disconnected)
        {
            lock (_monitoringLock)
            {
                _monitoringStatus = _monitoringStatus with
                {
                    BatteryPercentage = null,
                    IsConnected = false,
                    DevicePath = string.Empty,
                    Transport = "disconnected",
                    ConnectionMode = "disconnected",
                    DeviceInstanceId = string.Empty,
                    Manufacturer = string.Empty,
                    Product = string.Empty,
                    PowerCaptureState = "disconnected"
                };
            }
        }
    }

    private static async Task<int?> TryReadBatteryPercentageAsync(HidStream stream, CancellationToken cancellationToken)
    {
        byte[] setFeature = new byte[65];
        setFeature[1] = 0xF7;
        stream.SetFeature(setFeature);

        await Task.Delay(30, cancellationToken);

        byte[] response = new byte[65];
        response[0] = 0x05;
        stream.GetFeature(response);

        if (response[0] != 0x05 || response[1] != 0x00 || response[2] != 0x00)
        {
            return null;
        }

        int percent = response[3];
        return percent is >= 0 and <= 100 ? percent : null;
    }

    private void RefreshActivityStateFromSystemInput(DateTimeOffset now)
    {
        lock (_monitoringLock)
        {
            if (_currentSnapshot.Mode == AjazzConnectionMode.Disconnected)
            {
                _monitoringStatus = _monitoringStatus with { ActivityCaptureState = "disconnected" };
                return;
            }

            DateTimeOffset lastInputUtc = GetLastInputUtc(now);
            bool hadInputBefore = _lastSystemInputUtc != DateTimeOffset.MinValue;
            bool sawNewInput = !hadInputBefore || lastInputUtc > _lastSystemInputUtc;
            bool wasSleeping = string.Equals(_monitoringStatus.ActivityCaptureState, "actual-sleep", StringComparison.Ordinal);

            if (sawNewInput)
            {
                _lastSystemInputUtc = lastInputUtc;
                _monitoringStatus = _monitoringStatus with { LastActivityReadUtc = lastInputUtc, IsConnected = true };

                if (wasSleeping)
                {
                    _wakeAfterMovementUntilUtc = now.AddSeconds(3);
                }
            }

            TimeSpan sinceInput = now >= _lastSystemInputUtc && _lastSystemInputUtc != DateTimeOffset.MinValue
                ? now - _lastSystemInputUtc
                : TimeSpan.MaxValue;

            string state;
            if (_wakeAfterMovementUntilUtc.HasValue && _wakeAfterMovementUntilUtc.Value > now && sinceInput <= TimeSpan.FromSeconds(5))
            {
                state = "wake-after-movement";
            }
            else
            {
                _wakeAfterMovementUntilUtc = null;

                if (sinceInput <= TimeSpan.FromSeconds(2))
                {
                    state = "awake-and-moving";
                }
                else if (sinceInput <= TimeSpan.FromSeconds(45))
                {
                    state = "idle-but-awake";
                }
                else
                {
                    state = "actual-sleep";
                }
            }

            if (!ActivityCaptureStates.Contains(state))
            {
                state = "idle-but-awake";
            }

            _monitoringStatus = _monitoringStatus with { ActivityCaptureState = state };

            if (_currentSnapshot.Mode == AjazzConnectionMode.Unknown)
            {
                string derivedPower = state switch
                {
                    "awake-and-moving" or "wake-after-movement" => "awake-off-dock",
                    "idle-but-awake" => "idle-off-dock",
                    _ => "sleeping-off-dock"
                };

                if (PowerCaptureStates.Contains(derivedPower))
                {
                    _monitoringStatus = _monitoringStatus with { PowerCaptureState = derivedPower };
                }
            }
        }
    }

    private static DateTimeOffset GetLastInputUtc(DateTimeOffset now)
    {
        LastInputInfo info = new()
        {
            cbSize = (uint)Marshal.SizeOf<LastInputInfo>()
        };

        if (!GetLastInputInfo(ref info))
        {
            throw new InvalidOperationException("GetLastInputInfo failed.");
        }

        ulong tickCount = GetTickCount64();
        ulong lastInputTick = info.dwTime;
        ulong elapsedMilliseconds = tickCount >= lastInputTick ? tickCount - lastInputTick : 0;
        return now - TimeSpan.FromMilliseconds(elapsedMilliseconds);
    }

    private void OnDeviceSnapshotChanged(object? sender, AjazzDeviceSnapshot snapshot)
    {
        if (_stoppingToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            AjazzDeviceSnapshot previous = _currentSnapshot;
            _currentSnapshot = snapshot;

            if (previous.Mode == snapshot.Mode
                && string.Equals(previous.DeviceInterfacePath, snapshot.DeviceInterfacePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(previous.DeviceInstanceId, snapshot.DeviceInstanceId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (previous.Mode is AjazzConnectionMode.Dock or AjazzConnectionMode.Direct)
            {
                LogRemoval(previous);
            }

            if (snapshot.Mode is AjazzConnectionMode.Dock or AjazzConnectionMode.Direct)
            {
                ApplyConnectedSnapshot(snapshot);
                LogArrival(snapshot);
                QueueSyncAfterConnect();
            }
            else
            {
                ApplyDisconnectedSnapshot(previous.Mode);
            }

            if (previous.Mode is AjazzConnectionMode.Dock or AjazzConnectionMode.Direct
                && snapshot.Mode is AjazzConnectionMode.Dock or AjazzConnectionMode.Direct
                && previous.Mode != snapshot.Mode)
            {
                string transition = $"{GetConnectionModeKey(previous.Mode)}-to-{GetConnectionModeKey(snapshot.Mode)}";
                lock (_monitoringLock)
                {
                    _monitoringStatus = _monitoringStatus with { LastConnectionTransition = transition };
                }

                logger.LogWarning("MouseConnectionChanged Previous={Previous} Current={Current}", previous.Mode, snapshot.Mode);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(DeviceChangeErrorEvent, ex, "Unexpected error while handling device-interface change.");
        }
    }

    private void ApplyConnectedSnapshot(AjazzDeviceSnapshot snapshot)
    {
        lock (_monitoringLock)
        {
            _monitoringStatus = _monitoringStatus with
            {
                IsConnected = true,
                Transport = snapshot.Mode == AjazzConnectionMode.Dock ? "dock" : "usb",
                ConnectionMode = GetConnectionModeKey(snapshot.Mode),
                DevicePath = !string.IsNullOrWhiteSpace(snapshot.ControlInterfacePath) ? snapshot.ControlInterfacePath : snapshot.DeviceInterfacePath,
                DeviceInstanceId = snapshot.DeviceInstanceId,
                Manufacturer = snapshot.Manufacturer,
                Product = snapshot.Product,
                PowerCaptureState = ResolvePowerState(snapshot.Mode, _monitoringStatus.ActivityCaptureState, _monitoringStatus.BatteryPercentage, _lastBatteryPercentage, connected: true)
            };
        }
    }

    private void ApplyDisconnectedSnapshot(AjazzConnectionMode previousMode)
    {
        lock (_monitoringLock)
        {
            _monitoringStatus = _monitoringStatus with
            {
                IsConnected = false,
                BatteryPercentage = null,
                DevicePath = string.Empty,
                DeviceInstanceId = string.Empty,
                Manufacturer = string.Empty,
                Product = string.Empty,
                Transport = "disconnected",
                ConnectionMode = "disconnected",
                PowerCaptureState = "disconnected",
                ActivityCaptureState = "disconnected",
                LastConnectionTransition = previousMode is AjazzConnectionMode.Dock or AjazzConnectionMode.Direct
                    ? $"{GetConnectionModeKey(previousMode)}-to-disconnected"
                    : _monitoringStatus.LastConnectionTransition
            };
        }
    }

    private void LogArrival(AjazzDeviceSnapshot snapshot)
    {
        logger.LogWarning(
            "{Timestamp:HH:mm:ss.fff} ARRIVAL VID={VendorId:X4} PID={ProductId:X4} MODE={Mode} PATH={DevicePath} INSTANCE={InstanceId} MFG={Manufacturer} PRODUCT={Product}",
            snapshot.TimestampUtc.LocalDateTime,
            snapshot.VendorId,
            snapshot.ProductId,
            snapshot.Mode,
            !string.IsNullOrWhiteSpace(snapshot.ControlInterfacePath) ? snapshot.ControlInterfacePath : snapshot.DeviceInterfacePath,
            snapshot.DeviceInstanceId,
            snapshot.Manufacturer,
            snapshot.Product);
    }

    private void LogRemoval(AjazzDeviceSnapshot snapshot)
    {
        logger.LogWarning(
            "{Timestamp:HH:mm:ss.fff} REMOVAL VID={VendorId:X4} PID={ProductId:X4} MODE={Mode} PATH={DevicePath} INSTANCE={InstanceId} MFG={Manufacturer} PRODUCT={Product}",
            DateTimeOffset.Now.LocalDateTime,
            snapshot.VendorId,
            snapshot.ProductId,
            snapshot.Mode,
            !string.IsNullOrWhiteSpace(snapshot.ControlInterfacePath) ? snapshot.ControlInterfacePath : snapshot.DeviceInterfacePath,
            snapshot.DeviceInstanceId,
            snapshot.Manufacturer,
            snapshot.Product);
    }

    private void QueueSyncAfterConnect()
    {
        if (!settingsStore.GetSettings().SyncOnDeviceConnect)
        {
            logger.LogDebug("Device-connect sync is disabled by configuration.");
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
                logger.LogError(DeviceChangeErrorEvent, ex, "Unexpected error while handling device connect sync.");
            }
        }, _stoppingToken);
    }

    private async Task<bool> TrySyncAsync(string reason, DateTime targetDateTime, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            AjazzSettings settings = settingsStore.GetSettings();
            IEnumerable<HidDevice> candidates = GetSyncCandidates(settings, _currentSnapshot);

            foreach (HidDevice device in candidates)
            {
                try
                {
                    string productName = device.GetFriendlyName() ?? string.Empty;

                    logger.LogDebug("Found device ({Reason}): {ProductName} {DevicePath}", reason, productName, device.DevicePath);

                    if (!device.TryOpen(out HidStream? stream))
                    {
                        logger.LogDebug("Unable to open AJAZZ HID interface.");
                        continue;
                    }

                    using (stream)
                    {
                        byte[] payload = BuildTimePacket(targetDateTime);

                        logger.LogDebug("Syncing clock: {Timestamp:yyyy-MM-dd HH:mm:ss}", targetDateTime);
                        stream.SetFeature(payload);
                        logger.LogWarning(TimeSyncUpdatedEvent, "Clock sync succeeded ({Reason}) at {Timestamp:yyyy-MM-dd HH:mm:ss}.", reason, targetDateTime);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(TimeSyncErrorEvent, ex, "A matching interface rejected the time sync packet.");
                }
            }

            if (string.IsNullOrWhiteSpace(settings.SelectedDevicePath))
            {
                logger.LogDebug("No compatible AJAZZ interface accepted the time sync packet.");
            }
            else
            {
                logger.LogDebug("Selected AJAZZ device was not available or did not accept the sync packet.");
            }

            return false;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private static IEnumerable<HidDevice> GetAllAjazzControlInterfaces()
    {
        return DeviceList.Local.GetHidDevices().Where(IsAjazzControlInterface);
    }

    private static IEnumerable<HidDevice> GetSyncCandidates(AjazzSettings settings, AjazzDeviceSnapshot snapshot)
    {
        List<HidDevice> all = GetAllAjazzControlInterfaces().ToList();
        if (all.Count == 0)
        {
            return all;
        }

        IEnumerable<HidDevice> filtered = all;

        if (snapshot.Mode is AjazzConnectionMode.Dock or AjazzConnectionMode.Direct)
        {
            int productId = snapshot.ProductId;
            filtered = filtered.Where(d => d.ProductID == productId);

            if (!string.IsNullOrWhiteSpace(snapshot.ControlInterfacePath))
            {
                List<HidDevice> exact = filtered
                    .Where(d => string.Equals(d.DevicePath, snapshot.ControlInterfacePath, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (exact.Count > 0)
                {
                    return exact;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(settings.SelectedDevicePath))
        {
            return filtered;
        }

        string selected = settings.SelectedDevicePath.Trim();
        string selectedKey = GetDeviceIdentityKey(selected);
        List<HidDevice> matchingSelection = filtered
            .Where(d => string.Equals(d.DevicePath, selected, StringComparison.OrdinalIgnoreCase)
                || string.Equals(GetDeviceIdentityKey(d.DevicePath), selectedKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matchingSelection.Count > 0 ? matchingSelection : filtered;
    }

    private static string GetDeviceIdentityKey(string devicePath)
    {
        int idx = devicePath.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        return idx > 0 ? devicePath[..idx] : devicePath;
    }

    private static bool IsAjazzControlInterface(HidDevice device)
    {
        try
        {
            return device.VendorID == 0x3151
                && (device.ProductID == 0x5007 || device.ProductID == 0x4026)
                && device.DevicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetTransportKey(HidDevice device, AjazzDeviceSnapshot snapshot)
    {
        return snapshot.Mode switch
        {
            AjazzConnectionMode.Dock => "dock",
            AjazzConnectionMode.Direct => "usb",
            _ => device.ProductID switch
            {
                0x5007 => "dock",
                0x4026 => "usb",
                _ => "unknown"
            }
        };
    }

    private static string GetConnectionModeKey(AjazzConnectionMode mode)
    {
        return mode switch
        {
            AjazzConnectionMode.Dock => "dock",
            AjazzConnectionMode.Direct => "direct",
            AjazzConnectionMode.Disconnected => "disconnected",
            _ => "unknown"
        };
    }

    private static string FormatPowerState(string state)
    {
        return state switch
        {
            "awake-off-dock" => "Awake off dock",
            "idle-off-dock" => "Idle off dock",
            "sleeping-off-dock" => "Sleeping off dock",
            "placed-on-dock" => "Placed on dock",
            "charging-on-dock" => "Charging on dock",
            "disconnected" => "Disconnected",
            "usb-cable-connected" => "USB cable connected",
            "usb-cable-charging" => "Charging over USB cable",
            "fully-charged-on-dock" => "Fully charged on dock",
            "fully-charged-on-usb" => "Fully charged on USB cable",
            _ => state
        };
    }

    private static string FormatActivityState(string state)
    {
        return state switch
        {
            "awake-and-moving" => "Awake and moving",
            "idle-but-awake" => "Idle but awake",
            "actual-sleep" => "Sleeping",
            "wake-after-movement" => "Wake after movement",
            "disconnected" => "Disconnected",
            _ => state
        };
    }

    private static string FormatTransport(string transport)
    {
        return transport switch
        {
            "dock" => "Dock",
            "usb" => "Direct USB",
            "disconnected" => "Disconnected",
            "unknown" => "Unknown",
            _ => transport
        };
    }

    private static string FormatConnectionMode(string mode)
    {
        return mode switch
        {
            "dock" => "Dock",
            "direct" => "Direct USB",
            "disconnected" => "Disconnected",
            "unknown" => "Unknown",
            _ => mode
        };
    }

    private static string FormatConnectionTransition(string transition)
    {
        return transition switch
        {
            "dock-to-direct" => "Dock → Direct USB",
            "direct-to-dock" => "Direct USB → Dock",
            "dock-to-disconnected" => "Dock → Disconnected",
            "direct-to-disconnected" => "Direct USB → Disconnected",
            _ => string.IsNullOrWhiteSpace(transition) ? "--" : transition
        };
    }

    private static string ResolvePowerState(AjazzConnectionMode mode, string activityState, int? currentPercent, int? previousPercent, bool connected)
    {
        if (!connected || mode == AjazzConnectionMode.Disconnected)
        {
            return "disconnected";
        }

        if (mode == AjazzConnectionMode.Direct)
        {
            if (currentPercent.HasValue && currentPercent.Value >= 100)
            {
                return "fully-charged-on-usb";
            }

            if (currentPercent.HasValue && previousPercent.HasValue && currentPercent.Value > previousPercent.Value)
            {
                return "usb-cable-charging";
            }

            return "usb-cable-connected";
        }

        if (mode == AjazzConnectionMode.Dock)
        {
            if (currentPercent.HasValue && currentPercent.Value >= 100)
            {
                return "fully-charged-on-dock";
            }

            if (currentPercent.HasValue && previousPercent.HasValue && currentPercent.Value > previousPercent.Value)
            {
                return "charging-on-dock";
            }

            return "placed-on-dock";
        }

        return activityState switch
        {
            "actual-sleep" => "sleeping-off-dock",
            "idle-but-awake" => "idle-off-dock",
            _ => "awake-off-dock"
        };
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("kernel32.dll")]
    private static extern ulong GetTickCount64();

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
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
