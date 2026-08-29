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

    private Task PollBatteryOnceAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
            return Task.CompletedTask;
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

        return Task.CompletedTask;
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
                && (device.ProductID == 0x5007 || device.ProductID == 0x4026)
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
            if (device.VendorID != 0x3151 || (device.ProductID != 0x5007 && device.ProductID != 0x4026))
            {
                return false;
            }

            if (!device.DevicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase)
                && !device.DevicePath.Contains("&mi_01", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (snapshot.Mode is not AjazzConnectionMode.Dock and not AjazzConnectionMode.Direct)
            {
                return true;
            }

            return device.ProductID == snapshot.ProductId
                && string.Equals(GetDeviceIdentityKey(device.DevicePath), GetDeviceIdentityKey(snapshot.DeviceInterfacePath), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetInterfaceTag(string devicePath)
    {
        if (devicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase))
        {
            return "mi_00";
        }

        if (devicePath.Contains("&mi_01", StringComparison.OrdinalIgnoreCase))
        {
            return "mi_01";
        }

        if (devicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase))
        {
            return "mi_02";
        }

        return "unknown";
    }

    private static int GetInterfaceNumber(string devicePath)
    {
        if (devicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }

        if (devicePath.Contains("&mi_01", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        if (devicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        return -1;
    }

    private static int GetInterfaceNumber(string devicePath, string interfaceTag)
    {
        return interfaceTag switch
        {
            "mi_00" => 0,
            "mi_01" => 1,
            "mi_02" => 2,
            _ => GetInterfaceNumber(devicePath)
        };
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
        if (!TryParseId(settings.VendorId, out int expectedVendorId) || expectedVendorId != vendorId)
        {
            return false;
        }

        if (settings.ProductIds is null || settings.ProductIds.Length == 0)
        {
            return true;
        }

        foreach (string pid in settings.ProductIds)
        {
            if (TryParseId(pid, out int expectedProductId) && expectedProductId == productId)
            {
                return true;
            }
        }

        return false;
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
}
