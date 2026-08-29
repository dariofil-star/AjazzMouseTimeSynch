using System.Globalization;
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

public sealed record AjazzRawHidReport(
    DateTimeOffset TimestampUtc,
    string DevicePath,
    string InterfaceTag,
    int InterfaceNumber,
    int Endpoint,
    string Direction,
    int ReportId,
    int Length,
    string HexBytes,
    string Notes);

public sealed class AjazzClockSyncService(ILogger<AjazzClockSyncService> logger, IAjazzSettingsStore settingsStore, IHostEnvironment hostEnvironment) : BackgroundService
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

    private const int MaxRawReports = 512;

    private readonly SemaphoreSlim _syncLock = new(1, 1);
    private readonly Lock _monitoringLock = new();
    private readonly Lock _rawReportLock = new();
    private readonly AjazzDeviceMonitor _deviceMonitor = new(TimeSpan.FromMilliseconds(750));
    private readonly HidMouseReverseEngineeringEngine _reverseEngine = new();
    private readonly LinkedList<AjazzRawHidReport> _recentRawReports = [];

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

        try
        {
            _deviceMonitor.Start();
        }
        catch (Exception ex)
        {
            logger.LogError(DeviceChangeErrorEvent, ex, "Failed to start device monitor. Device monitoring will be unavailable.");
        }

        if (settingsStore.GetSettings().SyncOnStartup)
        {
            try
            {
                await TrySyncNowAsync("startup", stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(TimeSyncErrorEvent, ex, "Startup time sync failed.");
            }
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

    public IReadOnlyList<AjazzHidDeviceInfo> GetAllHidDevices()
    {
        return DeviceList.Local
            .GetHidDevices()
            .Select(device =>
            {
                string friendlyName;
                try
                {
                    friendlyName = device.GetFriendlyName() ?? string.Empty;
                }
                catch
                {
                    friendlyName = string.Empty;
                }

                return new AjazzHidDeviceInfo(
                    device.DevicePath,
                    friendlyName,
                    device.VendorID,
                    device.ProductID);
            })
            .OrderBy(device => device.VendorId)
            .ThenBy(device => device.ProductId)
            .ThenBy(device => device.ProductName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(device => device.DevicePath, StringComparer.OrdinalIgnoreCase)
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

    public IReadOnlyList<AjazzRawHidReport> GetRecentHidReports(int take)
    {
        int requested = take <= 0 ? 100 : Math.Min(take, 500);

        lock (_rawReportLock)
        {
            return _recentRawReports
                .TakeLast(requested)
                .Reverse()
                .ToList();
        }
    }

    public MouseState GetReverseEngineState()
    {
        return _reverseEngine.GetState();
    }

    public IReadOnlyList<HidInterfaceDescriptorSnapshot> GetDescriptorSnapshots()
    {
        return _reverseEngine.GetDescriptorSnapshots();
    }

    public IReadOnlyList<HidObservedReport> GetObservedReports(int take)
    {
        return _reverseEngine.GetRecentReports(take);
    }

    public IReadOnlyList<LabeledCapture> GetCaptureSessions()
    {
        return _reverseEngine.GetCompletedCaptures();
    }

    public void BeginCaptureSession(string label)
    {
        _reverseEngine.BeginCapture(label);
    }

    public void EndCaptureSession(string label)
    {
        _reverseEngine.EndCapture(label);
    }

    public CaptureDiffResult? DiffCaptureSessions(string leftLabel, string rightLabel, int reportId, int interfaceNumber, int endpoint)
    {
        return _reverseEngine.DiffCaptures(leftLabel, rightLabel, reportId, interfaceNumber, endpoint);
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
                DateTimeOffset now = DateTimeOffset.UtcNow;
                _reverseEngine.Tick(now);
                RefreshActivityStateFromSystemInput(now);
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
        cancellationToken.ThrowIfCancellationRequested();

        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            AjazzSettings settings = settingsStore.GetSettings();
            AjazzDeviceSnapshot snapshot = _currentSnapshot;
            List<HidDevice> candidates = GetSyncCandidates(settings, snapshot).ToList();

            bool capturedFeature = false;

            foreach (HidDevice device in candidates)
            {
                try
                {
                    _reverseEngine.RecordDescriptor(device, 2, 0x83);

                    if (!device.TryOpen(out HidStream? stream))
                    {
                        continue;
                    }

                    using (stream)
                    {
                        byte[]? feature = TryReadVendorFeatureReport(device, stream);
                        if (feature is null)
                        {
                            continue;
                        }

                        capturedFeature = true;
                        AddRawReport(device.DevicePath, "mi_02", "feature", 0, feature, "Vendor feature report (FF:02, 64 bytes payload)", device.VendorID, device.ProductID);
                        break;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Failed to read vendor feature report from control interface.");
                }
            }

            CaptureVendorInputReports(snapshot);

            if (capturedFeature)
            {
                SyncMonitoringFromReverseState();
                return;
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
        finally
        {
            _syncLock.Release();
        }
    }

    private void CaptureVendorInputReports(AjazzDeviceSnapshot snapshot)
    {
        if (snapshot.Mode == AjazzConnectionMode.Disconnected)
        {
            return;
        }

        List<HidDevice> devices = DeviceList.Local
            .GetHidDevices()
            .Where(d => IsAjazzInputOrVendorInterface(d, snapshot))
            .ToList();

        foreach (HidDevice device in devices)
        {
            try
            {
                int interfaceNumber = GetInterfaceNumber(device.DevicePath);
                int endpoint = GetEndpointByInterface(interfaceNumber);
                _reverseEngine.RecordDescriptor(device, interfaceNumber, endpoint);

                if (!device.TryOpen(out HidStream? stream))
                {
                    continue;
                }

                using (stream)
                {
                    stream.ReadTimeout = 20;
                    int maxLength = Math.Max(8, device.GetMaxInputReportLength());
                    byte[] buffer = new byte[maxLength];

                    for (int i = 0; i < 4; i++)
                    {
                        int read;
                        try
                        {
                            read = stream.Read(buffer, 0, buffer.Length);
                        }
                        catch (TimeoutException)
                        {
                            break;
                        }

                        if (read <= 0)
                        {
                            break;
                        }

                        byte[] payload = buffer[..read].ToArray();
                        string interfaceTag = GetInterfaceTag(device.DevicePath);
                        int reportId = ResolveInputReportId(interfaceTag, payload);
                        string notes = interfaceTag switch
                        {
                            "mi_00" => "Standard mouse report",
                            "mi_01" when reportId == 0 && payload.Length == 7 => "Standard mouse report (captured on MI_01)",
                            "mi_01" when reportId == 5 => "Vendor input report (Report ID 5, FF:01, 3-byte payload)",
                            _ => "Additional input report"
                        };

                        AddRawReport(device.DevicePath, interfaceTag, "input", reportId, payload, notes, device.VendorID, device.ProductID);
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Failed to capture input reports for reverse-engineering.");
            }
        }
    }

    private byte[]? TryReadVendorFeatureReport(HidDevice device, HidStream stream)
    {
        byte[] setFeature = new byte[65];
        setFeature[1] = 0xF7;
        DebugLogUsbTraffic("send", "SetFeature", device.DevicePath, device.VendorID, device.ProductID, "mi_02", "feature", 0, setFeature, "Battery feature query");
        stream.SetFeature(setFeature);

        byte[] response = new byte[65];
        response[0] = 0x05;
        stream.GetFeature(response);
        DebugLogUsbTraffic("receive", "GetFeature", device.DevicePath, device.VendorID, device.ProductID, "mi_02", "feature", 0, response, "Battery feature response");

        if (response.Length < 65)
        {
            return null;
        }

        return response;
    }

    private void AddRawReport(string devicePath, string interfaceTag, string direction, int reportId, byte[] bytes, string notes, int? vendorId = null, int? productId = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string hex = Convert.ToHexString(bytes);
        int interfaceNumber = GetInterfaceNumber(devicePath, interfaceTag);
        int endpoint = GetEndpointByInterface(interfaceNumber);

        AjazzRawHidReport report = new(
            now,
            devicePath,
            interfaceTag,
            interfaceNumber,
            endpoint,
            direction,
            reportId,
            bytes.Length,
            hex,
            notes);

        lock (_rawReportLock)
        {
            _recentRawReports.AddLast(report);
            while (_recentRawReports.Count > MaxRawReports)
            {
                _recentRawReports.RemoveFirst();
            }
        }

        HidReportDirection reportDirection = string.Equals(direction, "feature", StringComparison.OrdinalIgnoreCase)
            ? HidReportDirection.Feature
            : HidReportDirection.Input;

        int resolvedVendorId = vendorId ?? (TryParseVidPidFromDevicePath(devicePath, out int vidFromPath, out _) ? vidFromPath : 0);
        int resolvedProductId = productId ?? (TryParseVidPidFromDevicePath(devicePath, out _, out int pidFromPath) ? pidFromPath : 0);
        DebugLogUsbTraffic("receive", "InputReport", devicePath, resolvedVendorId, resolvedProductId, interfaceTag, direction, reportId, bytes, notes);

        IReadOnlyList<DecodedHidUsage> decoded = DecodeKnownUsages(interfaceNumber, reportId, bytes);
        bool isMovement = decoded.Any(d => (d.UsagePage == 0x01 && (d.Usage == 0x30 || d.Usage == 0x31) && d.Value != 0));
        bool isWheel = decoded.Any(d => (d.UsagePage == 0x01 && (d.Usage == 0x38 || d.Usage == 0x0238) && d.Value != 0));
        bool isButton = decoded.Any(d => d.UsagePage == 0x09 && d.Value != 0);
        bool isVendor = interfaceNumber == 1 && reportId == 5 || interfaceNumber == 2;

        MouseConnectionState connection = DeriveConnectionState(devicePath, _currentSnapshot.Mode);
        _reverseEngine.UpdateConnection(connection, $"report-path={devicePath}");

        _reverseEngine.RecordReport(new HidObservedReport(
            now,
            devicePath,
            GetDeviceIdentityKey(devicePath),
            connection,
            interfaceNumber,
            endpoint,
            reportDirection,
            reportId,
            bytes.Length,
            bytes,
            decoded,
            isMovement,
            isButton,
            isWheel,
            isVendor,
            notes));

        SyncMonitoringFromReverseState();

        if ((interfaceTag == "mi_01" && reportId == 5) || (interfaceTag == "mi_02" && direction == "feature"))
        {
            logger.LogInformation(
                "HID capture Interface={Interface} Endpoint=0x{Endpoint:X2} Direction={Direction} ReportId={ReportId} Length={Length} Data={Data}",
                interfaceTag,
                endpoint,
                direction,
                reportId,
                bytes.Length,
                hex);
        }
    }

    private void SyncMonitoringFromReverseState()
    {
        MouseState state = _reverseEngine.GetState();

        lock (_monitoringLock)
        {
            string connectionMode = state.ConnectionMode switch
            {
                MouseConnectionState.Dock => "dock",
                MouseConnectionState.DirectUsb => "direct",
                _ => "disconnected"
            };

            string transport = state.ConnectionMode switch
            {
                MouseConnectionState.Dock => "dock",
                MouseConnectionState.DirectUsb => "usb",
                _ => "disconnected"
            };

            string devicePath = _monitoringStatus.DevicePath;
            if (string.IsNullOrWhiteSpace(devicePath) && _recentRawReports.Count > 0)
            {
                devicePath = _recentRawReports.Last!.Value.DevicePath;
            }

            if (string.IsNullOrWhiteSpace(devicePath) && _currentSnapshot.Mode is AjazzConnectionMode.Dock or AjazzConnectionMode.Direct)
            {
                devicePath = !string.IsNullOrWhiteSpace(_currentSnapshot.ControlInterfacePath)
                    ? _currentSnapshot.ControlInterfacePath
                    : _currentSnapshot.DeviceInterfacePath;
            }

            string inferredProduct = _currentSnapshot.Product;
            if (string.IsNullOrWhiteSpace(inferredProduct) && !string.IsNullOrWhiteSpace(devicePath))
            {
                inferredProduct = devicePath.Contains("pid_5007", StringComparison.OrdinalIgnoreCase)
                    ? "AJAZZ (Dock Receiver)"
                    : devicePath.Contains("pid_4026", StringComparison.OrdinalIgnoreCase)
                        ? "AJAZZ (Direct USB)"
                        : string.Empty;
            }

            _monitoringStatus = _monitoringStatus with
            {
                BatteryPercentage = state.BatteryPercent,
                LastBatteryReadUtc = state.LastBatteryUpdate,
                LastActivityReadUtc = state.LastActivity,
                IsConnected = state.ConnectionMode != MouseConnectionState.Disconnected,
                ConnectionMode = connectionMode,
                Transport = transport,
                DevicePath = devicePath,
                DeviceInstanceId = !string.IsNullOrWhiteSpace(_currentSnapshot.DeviceInstanceId)
                    ? _currentSnapshot.DeviceInstanceId
                    : (!string.IsNullOrWhiteSpace(devicePath) ? GetDeviceIdentityKey(devicePath) : _monitoringStatus.DeviceInstanceId),
                Manufacturer = !string.IsNullOrWhiteSpace(_currentSnapshot.Manufacturer) ? _currentSnapshot.Manufacturer : "AJAZZ",
                Product = !string.IsNullOrWhiteSpace(inferredProduct) ? inferredProduct : _monitoringStatus.Product,
                PowerCaptureState = state.DerivedState,
                ActivityCaptureState = state.ActivityState switch
                {
                    MouseActivityState.AwakeAndMoving => "awake-and-moving",
                    MouseActivityState.IdleButAwake => "idle-but-awake",
                    MouseActivityState.ActualSleep => "actual-sleep",
                    MouseActivityState.WakeAfterMovement => "wake-after-movement",
                    _ => _monitoringStatus.ActivityCaptureState
                }
            };
        }
    }

    private void RefreshActivityStateFromSystemInput(DateTimeOffset now)
    {
        MouseState reverseState = _reverseEngine.GetState();
        if (reverseState.ConnectionMode != MouseConnectionState.Disconnected)
        {
            SyncMonitoringFromReverseState();
            return;
        }

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

            _reverseEngine.UpdateConnection(
                MapConnectionState(snapshot.Mode),
                $"VID={snapshot.VendorId:X4} PID={snapshot.ProductId:X4} mode={snapshot.Mode} path={snapshot.DeviceInterfacePath}");
            SyncMonitoringFromReverseState();

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
                        DebugLogUsbTraffic("send", "SetFeature", device.DevicePath, device.VendorID, device.ProductID, "mi_02", "feature", payload[0], payload, $"Clock sync packet ({reason})");
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
                && device.DevicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsAjazzInputOrVendorInterface(HidDevice device, AjazzDeviceSnapshot snapshot)
    {
        try
        {
            if (device.VendorID != 0x3151)
            {
                return false;
            }

            if (!device.DevicePath.Contains("&mi_", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (snapshot.Mode is not AjazzConnectionMode.Dock and not AjazzConnectionMode.Direct)
            {
                return true;
            }

            return string.Equals(GetDeviceIdentityKey(device.DevicePath), GetDeviceIdentityKey(snapshot.DeviceInterfacePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetInterfaceTag(string devicePath)
    {
        return TryGetInterfaceNumberFromDevicePath(devicePath, out int interfaceNumber)
            ? $"mi_{interfaceNumber:00}"
            : "unknown";
    }

    private static int GetInterfaceNumber(string devicePath)
    {
        return TryGetInterfaceNumberFromDevicePath(devicePath, out int interfaceNumber)
            ? interfaceNumber
            : -1;
    }

    private static int GetInterfaceNumber(string devicePath, string interfaceTag)
    {
        if (interfaceTag.StartsWith("mi_", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(interfaceTag.AsSpan(3), NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
        {
            return parsed;
        }

        return GetInterfaceNumber(devicePath);
    }

    private static bool TryGetInterfaceNumberFromDevicePath(string devicePath, out int interfaceNumber)
    {
        interfaceNumber = -1;
        int idx = devicePath.IndexOf("&mi_", StringComparison.OrdinalIgnoreCase);
        if (idx < 0 || devicePath.Length < idx + 8)
        {
            return false;
        }

        return int.TryParse(devicePath.AsSpan(idx + 4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out interfaceNumber);
    }

    private static int ResolveInputReportId(string interfaceTag, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return 0;
        }

        if (interfaceTag == "mi_00")
        {
            return 0;
        }

        if (interfaceTag == "mi_01" && payload.Length == 7)
        {
            return 0;
        }

        return payload[0];
    }

    private static int GetEndpointByInterface(int interfaceNumber)
    {
        return interfaceNumber switch
        {
            0 => 0x81,
            1 => 0x82,
            2 => 0x83,
            _ => 0
        };
    }

    private static MouseConnectionState MapConnectionState(AjazzConnectionMode mode)
    {
        return mode switch
        {
            AjazzConnectionMode.Dock => MouseConnectionState.Dock,
            AjazzConnectionMode.Direct => MouseConnectionState.DirectUsb,
            _ => MouseConnectionState.Disconnected
        };
    }

    private static MouseConnectionState DeriveConnectionState(string devicePath, AjazzConnectionMode snapshotMode)
    {
        if (devicePath.Contains("pid_5007", StringComparison.OrdinalIgnoreCase))
        {
            return MouseConnectionState.Dock;
        }

        if (devicePath.Contains("pid_4026", StringComparison.OrdinalIgnoreCase))
        {
            return MouseConnectionState.DirectUsb;
        }

        MouseConnectionState fromSnapshot = MapConnectionState(snapshotMode);
        return fromSnapshot != MouseConnectionState.Disconnected
            ? fromSnapshot
            : MouseConnectionState.Disconnected;
    }

    private static bool TryDecodeStandardMouseUsages(int interfaceNumber, int reportId, byte[] payload, List<DecodedHidUsage> usages)
    {
        int start;

        if (interfaceNumber == 0)
        {
            if (payload.Length < 7)
            {
                return false;
            }

            start = 0;
        }
        else if (interfaceNumber == 1)
        {
            if (reportId == 0 && payload.Length == 7)
            {
                start = 0;
            }
            else if (payload.Length >= 8 && payload[0] == reportId)
            {
                start = 1;
            }
            else
            {
                return false;
            }
        }
        else
        {
            return false;
        }

        if (payload.Length < start + 7)
        {
            return false;
        }

        byte buttons = payload[start];
        for (int bit = 0; bit < 5; bit++)
        {
            long value = (buttons >> bit) & 0x01;
            usages.Add(new DecodedHidUsage(0x09, (ushort)(bit + 1), value, bit, 1, true, "button"));
        }

        short x = BitConverter.ToInt16(payload, start + 1);
        short y = BitConverter.ToInt16(payload, start + 3);
        sbyte wheel = unchecked((sbyte)payload[start + 5]);
        sbyte pan = unchecked((sbyte)payload[start + 6]);

        usages.Add(new DecodedHidUsage(0x01, 0x30, x, (start + 1) * 8, 16, true, "X"));
        usages.Add(new DecodedHidUsage(0x01, 0x31, y, (start + 3) * 8, 16, true, "Y"));
        usages.Add(new DecodedHidUsage(0x01, 0x38, wheel, (start + 5) * 8, 8, true, "Wheel"));
        usages.Add(new DecodedHidUsage(0x0C, 0x0238, pan, (start + 6) * 8, 8, true, "AC Pan"));

        return true;
    }

    private static IReadOnlyList<DecodedHidUsage> DecodeKnownUsages(int interfaceNumber, int reportId, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return [];
        }

        List<DecodedHidUsage> usages = [];

        if ((interfaceNumber == 0 || interfaceNumber == 1) && TryDecodeStandardMouseUsages(interfaceNumber, reportId, payload, usages))
        {
            return usages;
        }

        if (interfaceNumber == 1 && reportId == 2 && payload.Length >= 2)
        {
            byte bits = payload[1];
            usages.Add(new DecodedHidUsage(0x01, 0x81, bits & 0x01, 0, 1, true, "System control 0x81"));
            usages.Add(new DecodedHidUsage(0x01, 0x82, (bits >> 1) & 0x01, 1, 1, true, "System control 0x82"));
            usages.Add(new DecodedHidUsage(0x01, 0x83, (bits >> 2) & 0x01, 2, 1, true, "System control 0x83"));
            return usages;
        }

        if (interfaceNumber == 1 && reportId == 3 && payload.Length >= 3)
        {
            ushort consumer = BitConverter.ToUInt16(payload, 1);
            usages.Add(new DecodedHidUsage(0x0C, 0x0001, consumer, 8, 16, true, "Consumer control"));
            return usages;
        }

        if (interfaceNumber == 1 && reportId == 5)
        {
            for (int i = 1; i < payload.Length; i++)
            {
                usages.Add(new DecodedHidUsage(0xFFFF, (ushort)i, payload[i], i * 8, 8, false, "Vendor input byte"));
            }

            return usages;
        }

        if (interfaceNumber == 2)
        {
            for (int i = 0; i < payload.Length; i++)
            {
                usages.Add(new DecodedHidUsage(0xFFFF, (ushort)i, payload[i], i * 8, 8, false, "Vendor feature byte"));
            }
        }

        return usages;
    }

    private void DebugLogUsbTraffic(string flow, string operation, string devicePath, int vendorId, int productId, string interfaceTag, string direction, int reportId, byte[] payload, string notes)
    {
        AjazzDebugLoggingSettings settings = settingsStore.GetSettings().DebugLogging;
        if (!settings.Enabled)
        {
            return;
        }

        if (!ShouldDebugLogDevice(settings, vendorId, productId))
        {
            return;
        }

        string logPath = settings.LogFilePath;
        if (!Path.IsPathRooted(logPath))
        {
            logPath = Path.Combine(hostEnvironment.ContentRootPath, logPath);
        }

        string? directory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string line = $"{DateTimeOffset.UtcNow:O}\t{flow}\t{operation}\tVID=0x{vendorId:X4}\tPID=0x{productId:X4}\tInterface={interfaceTag}\tDirection={direction}\tReportId={reportId}\tLength={payload.Length}\tPath={devicePath}\tData={Convert.ToHexString(payload)}\tNotes={notes}";

        try
        {
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to write debug USB log entry to {LogPath}", logPath);
        }
    }

    private static bool ShouldDebugLogDevice(AjazzDebugLoggingSettings settings, int vendorId, int productId)
    {
        return TryParseId(settings.VendorId, out int expectedVendorId)
            && expectedVendorId == vendorId;
    }

    private static bool TryParseVidPidFromDevicePath(string devicePath, out int vendorId, out int productId)
    {
        vendorId = 0;
        productId = 0;

        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        int vidIndex = devicePath.IndexOf("vid_", comparison);
        int pidIndex = devicePath.IndexOf("pid_", comparison);

        if (vidIndex < 0 || pidIndex < 0 || devicePath.Length < vidIndex + 8 || devicePath.Length < pidIndex + 8)
        {
            return false;
        }

        string vidText = devicePath.Substring(vidIndex + 4, 4);
        string pidText = devicePath.Substring(pidIndex + 4, 4);

        bool parsedVid = int.TryParse(vidText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vendorId);
        bool parsedPid = int.TryParse(pidText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out productId);
        return parsedVid && parsedPid;
    }

    private static bool TryParseId(string value, out int parsed)
    {
        parsed = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        if (trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(trimmed[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out parsed);
        }

        return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed);
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

    private static string GetTransportKeyFromSnapshot(AjazzDeviceSnapshot snapshot)
    {
        return snapshot.Mode switch
        {
            AjazzConnectionMode.Dock => "dock",
            AjazzConnectionMode.Direct => "usb",
            AjazzConnectionMode.Disconnected => "disconnected",
            _ => "unknown"
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
            "dock" => "2.4G Receiver",
            "usb" => "USB HID",
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

    // ── Standard feature-report helpers ──────────────────────────────────────

    /// <summary>
    /// Builds a 65-byte HID feature report using the AJ-series standard envelope:
    /// [0]=0x05 (report-id), [1]=opcode, [2..63]=payload filled by <paramref name="fill"/>,
    /// [64]=BIT7 checksum (sum of bytes [1..63] &amp; 0x7F).
    /// </summary>
    private static byte[] BuildFeatureRequest(byte opcode, Action<byte[]>? fill = null)
    {
        byte[] buf = new byte[65];
        buf[0] = 0x05;
        buf[1] = opcode;
        fill?.Invoke(buf);
        buf[64] = ComputeBit7Checksum(buf);
        return buf;
    }

    private static byte[] BuildYichipRequest(byte opcode)
    {
        byte[] buf = new byte[33];
        buf[0] = 0x00;
        buf[1] = opcode;
        return buf;
    }

    /// <summary>BIT7 checksum: sum of buf[1..63] &amp; 0x7F, stored at buf[64].</summary>
    private static byte ComputeBit7Checksum(byte[] buf)
    {
        int sum = 0;
        for (int i = 1; i < 64; i++)
        {
            sum += buf[i];
        }

        return (byte)(sum & 0x7F);
    }

    private static bool IsBatteryStatusFrame(byte[] response)
    {
        return response.Length >= 8
            && response[0] == 0x05
            && response[1] == 0x00
            && response[2] == 0x00
            && response[4] == 0x01
            && response[7] == 0x02;
    }

    private static bool HasAnyNonZeroPayload(byte[] response, int startIndex = 1, int endExclusive = 64)
    {
        for (int i = startIndex; i < endExclusive && i < response.Length; i++)
        {
            if (response[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Detects transient HID errors where SetFeature fails with IOException but the Win32 
    /// error code is ERROR_SUCCESS (0). This is a known issue with certain HID devices and timing.
    /// </summary>
    private static bool IsTransientHidError(IOException ioEx)
    {
        try
        {
            // Check if inner exception is Win32Exception with error code 0 (ERROR_SUCCESS)
            if (ioEx.InnerException is System.ComponentModel.Win32Exception win32Ex)
            {
                // NativeErrorCode 0 means ERROR_SUCCESS - a paradoxical but known HID timing issue
                return win32Ex.NativeErrorCode == 0;
            }

            // Also check if the IOException itself might have HResult indicating success
            // Some HID libraries wrap errors differently
            if (ioEx.HResult == unchecked((int)0x80070000) || // Generic I/O error with ERROR_SUCCESS
                (ioEx.Message?.Contains("operation completed successfully", StringComparison.OrdinalIgnoreCase) == true))
            {
                return true;
            }
        }
        catch
        {
            // If we can't determine the error type safely, treat it as non-transient
            return false;
        }
        return false;
    }

    private static bool LooksLikeDpiOptionParamResponse(byte[] response)
    {
        if (response.Length < 25)
        {
            return false;
        }

        int stageCount = response[4];
        if (stageCount is < 1 or > 8)
        {
            return false;
        }

        return HasAnyNonZeroPayload(response, 9, 25);
    }

    private static bool LooksLikeMouseOptionParamResponse(byte[] response)
    {
        if (response.Length < 62)
        {
            return false;
        }

        bool validProfile = (response[9] & 0xF8) == 0;
        bool validLiftOff = response[53] <= 2;
        return validProfile && validLiftOff;
    }

    private static bool IsEchoOnlyOpcodeResponse(byte[] response, byte opcode)
    {
        if (response.Length < 65 || response[0] != 0x05 || response[1] != opcode)
        {
            return false;
        }

        for (int i = 2; i < 64; i++)
        {
            if (response[i] != 0)
            {
                return false;
            }
        }

        return response[64] == (opcode & 0x7F);
    }

    private static bool TryParseYichipFirmware(byte[] response, out ushort raw)
    {
        raw = 0;
        if (response.Length < 4)
        {
            return false;
        }

        if (response[0] == 0x00 && response[1] == 0x80 && (response[2] != 0 || response[3] != 0))
        {
            raw = (ushort)(response[2] | (response[3] << 8));
            return true;
        }

        if (response[1] != 0 && response[2] != 0)
        {
            raw = (ushort)(response[1] | (response[2] << 8));
            return true;
        }

        return false;
    }

    private static AjazzDpiTableRequest? TryParseYichipDpiTable(byte[] response, int profileIndex)
    {
        if (response.Length < 30 || response[4] != 0x25)
        {
            return null;
        }

        int packed = response[5];
        int activeStage = (packed >> 4) & 0x0F;
        int stageCount = packed & 0x0F;
        if (stageCount <= 0 || stageCount > 8 || activeStage >= stageCount)
        {
            return null;
        }

        var table = new AjazzDpiTableRequest
        {
            ProfileIndex = profileIndex,
            ActiveStage = activeStage,
            StageCount = stageCount,
            DpiValues = new int[8],
            Colors = Enumerable.Range(0, 8).Select(_ => new AjazzDpiStageColor()).ToArray()
        };

        for (int i = 0; i < stageCount; i++)
        {
            int baseIndex = 6 + (i * 4);
            if (baseIndex + 1 >= response.Length)
            {
                return null;
            }

            int wire = response[baseIndex] | (response[baseIndex + 1] << 8);
            if (wire < 0 || wire > 600)
            {
                return null;
            }

            table.DpiValues[i] = (wire + 1) * 50;
        }

        return table;
    }

    /// <summary>
    /// Encodes an <see cref="AjazzLedSetting"/> into 8 bytes at <paramref name="buf"/>[<paramref name="offset"/>..offset+7].
    /// Layout: [0]=effectType [1]=wireSpeed [2]=brightness [3]=optionByte [4]=R [5]=G [6]=B [7]=0.
    /// </summary>
    private static void EncodeLedBlock(byte[] buf, int offset, AjazzLedSetting s)
    {
        int wireSpeed = Math.Clamp(4 - s.Speed, 0, 4);
        int dazzle = s.Dazzle ? 1 : 0;
        int modeBits = dazzle != 0 ? 8 : 7;
        int optionByte = (s.Option << 4) | modeBits;

        buf[offset]     = (byte)s.EffectType;
        buf[offset + 1] = (byte)wireSpeed;
        buf[offset + 2] = (byte)s.Brightness;
        buf[offset + 3] = (byte)optionByte;
        buf[offset + 4] = s.R;
        buf[offset + 5] = s.G;
        buf[offset + 6] = s.B;
        buf[offset + 7] = 0;
    }

    /// <summary>Decodes 8 bytes at <paramref name="buf"/>[<paramref name="offset"/>] into an <see cref="AjazzLedSetting"/>.</summary>
    private static AjazzLedSetting DecodeLedBlock(byte[] buf, int offset)
    {
        int optionByte = buf[offset + 3];
        int modeBits = optionByte & 0x0F;
        bool dazzle = modeBits == 8;
        int option = (optionByte >> 4) & 0x0F;
        int wireSpeed = buf[offset + 1];
        int uiSpeed = Math.Clamp(4 - wireSpeed, 0, 4);

        return new AjazzLedSetting
        {
            EffectType = buf[offset],
            Speed      = uiSpeed,
            Brightness = buf[offset + 2],
            Dazzle     = dazzle,
            Option     = option,
            R          = buf[offset + 4],
            G          = buf[offset + 5],
            B          = buf[offset + 6]
        };
    }

    // ── Generic device-command executor ──────────────────────────────────────

    // Maximum time to wait for the dongle to relay a command over 2.4GHz and cache the response.
    private static readonly TimeSpan FeatureResponseTimeout = TimeSpan.FromMilliseconds(300);
    // Interval between GetFeature polls while waiting for a non-empty response.
    private static readonly TimeSpan FeaturePollInterval = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Opens the first available AJ-series control interface (MI_02), sends
    /// <paramref name="request"/> via SetFeature, and—if <paramref name="responseReportId"/>
    /// is non-null—polls GetFeature until the response payload is non-zero or timeout expires.
    /// This handles the dock's wireless round-trip latency: the dongle queues the 2.4GHz command
    /// and returns all-zero immediately if polled too quickly; we retry until data arrives.
    /// Returns <c>null</c> on failure or when no device is available.
    /// When <paramref name="requireDirectUsb"/> is <c>true</c> only PID 0x4026
    /// (direct USB dock path) is used; falls back to full candidate list otherwise.
    /// </summary>
    private async Task<byte[]?> ExecuteFeatureCommandAsync(
        byte[] request,
        byte? responseReportId,
        string operationName,
        CancellationToken cancellationToken)
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
                    if (!device.TryOpen(out HidStream? stream))
                    {
                        continue;
                    }

                    using (stream)
                    {
                        DebugLogUsbTraffic("send", "SetFeature", device.DevicePath,
                            device.VendorID, device.ProductID, "mi_02", "feature",
                            request[1], request, operationName);

                        stream.SetFeature(request);

                        if (responseReportId is null)
                        {
                            return [];
                        }

                        // Poll GetFeature until the dongle has the wireless response ready.
                        // The 2.4GHz round-trip typically takes 20-80 ms; polling avoids
                        // getting an all-zero echo on the first immediate read.
                        var deadline = DateTimeOffset.UtcNow.Add(FeatureResponseTimeout);
                        byte expectedOpcode = request[1];
                        byte[] response = new byte[65];
                        byte[]? matchedResponse = null;

                        while (DateTimeOffset.UtcNow < deadline)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            response[0] = responseReportId.Value;
                            stream.GetFeature(response);

                            bool opcodeMatches = response[1] == expectedOpcode;

                            if (!opcodeMatches && expectedOpcode == 0x80)
                            {
                                opcodeMatches = !IsBatteryStatusFrame(response) && HasAnyNonZeroPayload(response, 1, 64);
                            }
                            else if (!opcodeMatches && expectedOpcode == 0xD4)
                            {
                                opcodeMatches = !IsBatteryStatusFrame(response) && LooksLikeDpiOptionParamResponse(response);
                            }
                            else if (!opcodeMatches && expectedOpcode == 0xD3)
                            {
                                opcodeMatches = !IsBatteryStatusFrame(response) && LooksLikeMouseOptionParamResponse(response);
                            }

                            if (opcodeMatches)
                            {
                                matchedResponse = (byte[])response.Clone();

                                // Check whether the payload (bytes 2..63) is non-zero.
                                bool hasPayload = false;
                                for (int i = 2; i < 64; i++)
                                {
                                    if (response[i] != 0) { hasPayload = true; break; }
                                }

                                if (hasPayload)
                                {
                                    break;
                                }
                            }

                            await Task.Delay(FeaturePollInterval, cancellationToken);
                        }

                        if (matchedResponse is null)
                        {
                            continue;
                        }

                        DebugLogUsbTraffic("receive", "GetFeature", device.DevicePath,
                            device.VendorID, device.ProductID, "mi_02", "feature",
                            matchedResponse[1], matchedResponse, operationName + " response");

                        return matchedResponse;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Feature command {Operation} failed on {DevicePath}.",
                        operationName, device.DevicePath);
                }
            }

            return null;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task<byte[]?> ExecuteYichipCommandAsync(byte opcode, string operationName, CancellationToken cancellationToken)
    {
        await _syncLock.WaitAsync(cancellationToken);
        try
        {
            AjazzSettings settings = settingsStore.GetSettings();
            IEnumerable<HidDevice> candidates = GetSyncCandidates(settings, _currentSnapshot);
            byte[] request = BuildYichipRequest(opcode);

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
                        DebugLogUsbTraffic("send", "SetFeature", device.DevicePath,
                            device.VendorID, device.ProductID, "mi_02", "feature",
                            opcode, request, operationName);

                        // SetFeature can throw IOException with Win32Exception inner exception
                        // reporting ERROR_SUCCESS (0) - this is a known HID timing/state issue.
                        // Retry with exponential backoff to handle persistent timing issues.
                        bool setFeatureSuccess = false;
                        Exception? lastSetFeatureException = null;
                        const int maxAttempts = 4; // Increased from 2 to 4 attempts

                        for (int attempt = 0; attempt < maxAttempts && !setFeatureSuccess; attempt++)
                        {
                            try
                            {
                                if (attempt > 0)
                                {
                                    // Exponential backoff: 50ms, 100ms, 200ms
                                    int delayMs = 50 * (1 << (attempt - 1));
                                    await Task.Delay(delayMs, cancellationToken);
                                }
                                stream.SetFeature(request);
                                setFeatureSuccess = true;
                            }
                            catch (IOException ioEx)
                            {
                                // Check if this is a transient HID error (Win32 ERROR_SUCCESS paradox)
                                bool isTransient = IsTransientHidError(ioEx);

                                if (isTransient)
                                {
                                    lastSetFeatureException = ioEx;
                                    logger.LogDebug("SetFeature attempt {Attempt}/{MaxAttempts} encountered transient HID error (NativeErrorCode: {ErrorCode}) on {DevicePath}.", 
                                        attempt + 1,
                                        maxAttempts,
                                        ioEx.InnerException is System.ComponentModel.Win32Exception w32 ? w32.NativeErrorCode : -1,
                                        device.DevicePath);
                                    // Continue to next attempt
                                }
                                else
                                {
                                    // Not a transient error, rethrow immediately
                                    throw;
                                }
                            }
                        }

                        if (!setFeatureSuccess)
                        {
                            throw new IOException(
                                $"SetFeature failed after {maxAttempts} attempts on {device.DevicePath}", 
                                lastSetFeatureException);
                        }

                        var deadline = DateTimeOffset.UtcNow.Add(FeatureResponseTimeout);
                        int responseSize = Math.Max(33, device.GetMaxFeatureReportLength());
                        byte[] response = new byte[responseSize];
                        byte[]? matchedResponse = null;

                        while (DateTimeOffset.UtcNow < deadline)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            response[0] = 0x00;
                            stream.GetFeature(response);

                            bool looksLikeReply = response.Length > 5 && response[1] == opcode;
                            if (looksLikeReply)
                            {
                                matchedResponse = (byte[])response.Clone();
                                if (HasAnyNonZeroPayload(response, 2, response.Length))
                                {
                                    break;
                                }
                            }

                            await Task.Delay(FeaturePollInterval, cancellationToken);
                        }

                        if (matchedResponse is null)
                        {
                            continue;
                        }

                        DebugLogUsbTraffic("receive", "GetFeature", device.DevicePath,
                            device.VendorID, device.ProductID, "mi_02", "feature",
                            matchedResponse[1], matchedResponse, operationName + " response");

                        return matchedResponse;
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "Yichip command {Operation} failed on {DevicePath}.",
                        operationName, device.DevicePath);
                }
            }

            return null;
        }
        finally
        {
            _syncLock.Release();
        }
    }

    // ── Public opcode methods ─────────────────────────────────────────────────

    /// <summary>Queries firmware version via FEA_CMD_GET_REV (0x80).</summary>
    public async Task<AjazzFirmwareVersion?> TryGetFirmwareVersionAsync(CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0x80);
        byte[]? response = await ExecuteFeatureCommandAsync(request, 0x05, "GET_REV(0x80)", cancellationToken);

        if (response is not null && response.Length >= 4)
        {
            if (IsEchoOnlyOpcodeResponse(response, 0x80))
            {
                byte[]? yichipResponse = await ExecuteYichipCommandAsync(0x80, "YICHIP_GET_REV(0x80)", cancellationToken);
                if (yichipResponse is not null && TryParseYichipFirmware(yichipResponse, out ushort yichipRaw) && yichipRaw != 0)
                {
                    return new AjazzFirmwareVersion(yichipRaw, $"{yichipRaw >> 8}.{yichipRaw & 0xFF}");
                }

                return new AjazzFirmwareVersion(0, "unsupported (echo-only reply)");
            }

            // Compatibility decode:
            // - echo layout: response[1] == 0x80, version at response[2..3] (LE)
            // - compact layout: version at response[1..2] (LE)
            ushort raw = response[1] == 0x80
                ? BitConverter.ToUInt16(response, 2)
                : (ushort)(response[1] | (response[2] << 8));

            if (raw != 0)
            {
                return new AjazzFirmwareVersion(raw, $"{raw >> 8}.{raw & 0xFF}");
            }
        }

        byte[]? fallback = await ExecuteYichipCommandAsync(0x80, "YICHIP_GET_REV(0x80)", cancellationToken);
        if (fallback is not null && TryParseYichipFirmware(fallback, out ushort fallbackRaw) && fallbackRaw != 0)
        {
            return new AjazzFirmwareVersion(fallbackRaw, $"{fallbackRaw >> 8}.{fallbackRaw & 0xFF}");
        }

        return null;
    }

    /// <summary>Reads the currently active profile via FEA_CMD_GET_PROFILE (0x85).</summary>
    public async Task<int?> TryGetProfileAsync(CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0x85);
        byte[]? response = await ExecuteFeatureCommandAsync(request, 0x05, "GET_PROFILE(0x85)", cancellationToken);
        if (response is null || response.Length < 3 || response[1] != 0x85)
        {
            return null;
        }

        // body byte 1 = current profile → buf[2]
        return response[2] & 0x07;
    }

    /// <summary>Selects an active profile via FEA_CMD_SET_PROFILE (0x05).</summary>
    public async Task<bool> TrySetProfileAsync(int profileIndex, CancellationToken cancellationToken = default)
    {
        if (profileIndex is < 0 or > 7)
        {
            throw new ArgumentOutOfRangeException(nameof(profileIndex), "Profile index must be 0-7.");
        }

        byte[] request = BuildFeatureRequest(0x05, buf => buf[2] = (byte)profileIndex);
        byte[]? result = await ExecuteFeatureCommandAsync(request, null, "SET_PROFILE(0x05)", cancellationToken);
        return result is not null;
    }

    /// <summary>Sends a factory-reset command via FEA_CMD_SET_RESERT (0x02).</summary>
    public async Task<bool> TrySendFactoryResetAsync(CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0x02);
        byte[]? result = await ExecuteFeatureCommandAsync(request, null, "SET_RESERT(0x02)", cancellationToken);
        return result is not null;
    }

    /// <summary>Writes the LED setting via FEA_CMD_SET_LEDPARAM (0x07).</summary>
    public async Task<bool> TrySetLedParamAsync(AjazzLedSetting setting, CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0x07, buf => EncodeLedBlock(buf, 2, setting));
        byte[]? result = await ExecuteFeatureCommandAsync(request, null, "SET_LEDPARAM(0x07)", cancellationToken);
        return result is not null;
    }

    /// <summary>Reads the LED setting via FEA_CMD_GET_LEDPARAM (0x87).</summary>
    public async Task<AjazzLedSetting?> TryGetLedParamAsync(CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0x87);
        byte[]? response = await ExecuteFeatureCommandAsync(request, 0x05, "GET_LEDPARAM(0x87)", cancellationToken);
        if (response is null || response.Length < 10 || response[1] != 0x87)
        {
            return null;
        }

        // body byte 1 starts at buf[2]; LED block is 8 bytes
        return DecodeLedBlock(response, 2);
    }

    /// <summary>Writes the DPI table via FEA_CMD_MOUSE_SET_OPTIONPARAM1 (0x54).</summary>
    public async Task<bool> TrySetDpiTableAsync(AjazzDpiTableRequest table, CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0x54, buf =>
        {
            buf[2] = (byte)Math.Clamp(table.ProfileIndex, 0, 7);
            buf[3] = (byte)Math.Clamp(table.ActiveStage, 0, 7);
            buf[4] = (byte)Math.Clamp(table.StageCount, 0, 8);
            // bytes 5-8 = 0 (reserved)

            // bytes 9-24: 8 × uint16-LE DPI values (body bytes 8-23 → buf[9-24])
            int[] dpi = table.DpiValues ?? [];
            for (int i = 0; i < 8; i++)
            {
                int val = i < dpi.Length ? dpi[i] : 0;
                byte[] le = BitConverter.GetBytes((ushort)Math.Clamp(val, 0, 65535));
                buf[9 + i * 2]     = le[0];
                buf[9 + i * 2 + 1] = le[1];
            }

            // bytes 41-64: 8 × {R,G,B} (body bytes 40-63 → buf[41-64])
            // Note: buf[64] is checksum and will overwrite colour-7 B — vendor behaviour replicated.
            AjazzDpiStageColor[] colors = table.Colors ?? [];
            for (int i = 0; i < 8; i++)
            {
                AjazzDpiStageColor col = i < colors.Length ? colors[i] : new AjazzDpiStageColor();
                buf[41 + i * 3]     = col.R;
                buf[41 + i * 3 + 1] = col.G;
                buf[41 + i * 3 + 2] = col.B;
            }
        });

        byte[]? result = await ExecuteFeatureCommandAsync(request, null, "SET_OPTIONPARAM1(0x54)", cancellationToken);
        return result is not null;
    }

    /// <summary>Reads the DPI table via FEA_CMD_MOUSE_GET_OPTIONPARAM1 (0xD4).</summary>
    public async Task<AjazzDpiTableRequest?> TryGetDpiTableAsync(int profileIndex, CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0xD4, buf => buf[2] = (byte)Math.Clamp(profileIndex, 0, 7));
        byte[]? response = await ExecuteFeatureCommandAsync(request, 0x05, "GET_OPTIONPARAM1(0xD4)", cancellationToken);

        if (response is not null && response.Length >= 65 && !IsBatteryStatusFrame(response) && !IsEchoOnlyOpcodeResponse(response, 0xD4))
        {
            var table = new AjazzDpiTableRequest
            {
                ProfileIndex = profileIndex,
                ActiveStage  = response[3] & 0x07,   // body byte 2 → buf[3]
                StageCount   = response[4],          // body byte 3 → buf[4]
                DpiValues    = new int[8],
                Colors       = Enumerable.Range(0, 8).Select(_ => new AjazzDpiStageColor()).ToArray()
            };

            for (int i = 0; i < 8; i++)
            {
                table.DpiValues[i] = BitConverter.ToUInt16(response, 9 + i * 2);
            }

            for (int i = 0; i < 7; i++) // stage 8 B-channel is checksum on wire; only read 7
            {
                table.Colors[i].R = response[41 + i * 3];
                table.Colors[i].G = response[41 + i * 3 + 1];
                table.Colors[i].B = response[41 + i * 3 + 2];
            }

            bool hasAnyDpiValue = table.DpiValues.Any(v => v > 0);
            if (table.StageCount > 0 && hasAnyDpiValue)
            {
                return table;
            }
        }

        byte[]? yichip = await ExecuteYichipCommandAsync(0x13, "YICHIP_GET_DPI(0x13)", cancellationToken);
        return yichip is null ? null : TryParseYichipDpiTable(yichip, profileIndex);
    }

    /// <summary>Rebinds a single button via FEA_CMD_MOUSE_SET_KEYMATRIX (0x50).</summary>
    public async Task<bool> TrySetButtonBindAsync(AjazzButtonBindRequest bind, CancellationToken cancellationToken = default)
    {
        byte[] action = bind.ActionBytes ?? new byte[4];

        byte[] request = BuildFeatureRequest(0x50, buf =>
        {
            buf[2] = (byte)Math.Clamp(bind.ProfileIndex, 0, 7);
            buf[3] = (byte)bind.ButtonIndex;
            // bytes 4-8 = 0 (reserved)
            // body bytes 8-11 = action → buf[9-12]
            for (int i = 0; i < 4 && i < action.Length; i++)
            {
                buf[9 + i] = action[i];
            }
        });

        byte[]? result = await ExecuteFeatureCommandAsync(request, null, "SET_KEYMATRIX(0x50)", cancellationToken);
        return result is not null;
    }

    /// <summary>Writes the omnibus mouse settings packet via FEA_CMD_MOUSE_SET_OPTIONPARAM0 (0x53).</summary>
    public async Task<bool> TrySetMouseSettingsAsync(AjazzMouseSettingsRequest s, CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0x53, buf =>
        {
            // bytes 1-7: 0 (already zeroed)
            buf[9]  = (byte)Math.Clamp(s.ProfileIndex, 0, 7);   // body byte 8
            buf[10] = (byte)s.PollingRateCode;                   // body byte 9
            buf[11] = (byte)Math.Clamp(s.DebounceMs, 0, 255);   // body byte 10
            // body byte 11 = 0

            // body bytes 12-13: uint16-LE flags
            int flags = 0;
            if (s.LightOff)       flags |= 1;
            if (s.WheelLightOff)  flags |= 2;
            if (s.MotionSmoothing) flags |= 4;
            buf[13] = (byte)(flags & 0xFF);         // body byte 12 low
            buf[14] = (byte)((flags >> 8) & 0xFF);  // body byte 13 high

            // body byte 14 = 0
            buf[16] = (byte)s.WheelToButton;   // body byte 15
            buf[17] = (byte)s.ButtonToWheel;   // body byte 16
            // body bytes 17-23 = 0

            // body bytes 24-31: 8-byte main LED block → buf[25-32]
            EncodeLedBlock(buf, 25, s.Light ?? new AjazzLedSetting());
            // body bytes 32-39: 8-byte logo LED block → buf[33-40]
            EncodeLedBlock(buf, 33, s.LogoLight ?? new AjazzLedSetting());

            // body bytes 40-47: sleep times → buf[41-48]
            AjazzSleepTimes sleep = s.Sleep ?? new AjazzSleepTimes();
            byte[] timeBt    = BitConverter.GetBytes(sleep.IdleBtSeconds);
            byte[] deepBt    = BitConverter.GetBytes(sleep.DeepBtSeconds);
            byte[] time24    = BitConverter.GetBytes(sleep.Idle24gSeconds);
            byte[] deep24    = BitConverter.GetBytes(sleep.Deep24gSeconds);
            buf[41] = timeBt[0]; buf[42] = timeBt[1];
            buf[43] = deepBt[0]; buf[44] = deepBt[1];
            buf[45] = time24[0]; buf[46] = time24[1];
            buf[47] = deep24[0]; buf[48] = deep24[1];

            // body bytes 48-49 = 0
            buf[51] = (byte)Math.Clamp(s.XSensitivity, 0, 100);  // body byte 50
            buf[52] = (byte)Math.Clamp(s.YSensitivity, 0, 100);  // body byte 51
            buf[53] = (byte)Math.Clamp(s.LiftCutOff, 0, 2);      // body byte 52
            buf[54] = s.AngleSnap ? (byte)1 : (byte)0;            // body byte 53

            // battery colours body bytes 54-59 → buf[55-60]
            AjazzBatteryLedColors bc = s.BatteryColors ?? new AjazzBatteryLedColors();
            buf[55] = bc.HighR; buf[56] = bc.HighG; buf[57] = bc.HighB;
            buf[58] = bc.LowR;  buf[59] = bc.LowG;  buf[60] = bc.LowB;

            buf[61] = s.ChargingSwitch ? (byte)1 : (byte)0;  // body byte 60
        });

        byte[]? result = await ExecuteFeatureCommandAsync(request, null, "SET_OPTIONPARAM0(0x53)", cancellationToken);
        return result is not null;
    }

    /// <summary>Reads the omnibus mouse settings via FEA_CMD_MOUSE_GET_OPTIONPARAM0 (0xD3).</summary>
    public async Task<AjazzMouseSettingsRequest?> TryGetMouseSettingsAsync(CancellationToken cancellationToken = default)
    {
        byte[] request = BuildFeatureRequest(0xD3);
        byte[]? r = await ExecuteFeatureCommandAsync(request, 0x05, "GET_OPTIONPARAM0(0xD3)", cancellationToken);
        if (r is null || r.Length < 65 || IsBatteryStatusFrame(r) || IsEchoOnlyOpcodeResponse(r, 0xD3))
        {
            return null;
        }

        int flags = r[13] | (r[14] << 8);
        AjazzSleepTimes sleep = new()
        {
            IdleBtSeconds  = BitConverter.ToUInt16(r, 41),
            DeepBtSeconds  = BitConverter.ToUInt16(r, 43),
            Idle24gSeconds = BitConverter.ToUInt16(r, 45),
            Deep24gSeconds = BitConverter.ToUInt16(r, 47)
        };

        return new AjazzMouseSettingsRequest
        {
            ProfileIndex   = r[9] & 0x07,
            PollingRateCode = r[10],
            DebounceMs     = r[11],
            LightOff       = (flags & 1) != 0,
            WheelLightOff  = (flags & 2) != 0,
            MotionSmoothing = (flags & 4) != 0,
            WheelToButton  = r[16],
            ButtonToWheel  = r[17],
            Light          = DecodeLedBlock(r, 25),
            LogoLight      = DecodeLedBlock(r, 33),
            Sleep          = sleep,
            XSensitivity   = r[51],
            YSensitivity   = r[52],
            LiftCutOff     = r[53],
            AngleSnap      = r[54] != 0,
            BatteryColors  = new AjazzBatteryLedColors
            {
                HighR = r[55], HighG = r[56], HighB = r[57],
                LowR  = r[58], LowG  = r[59], LowB  = r[60]
            },
            ChargingSwitch = r[61] != 0
        };
    }
}
