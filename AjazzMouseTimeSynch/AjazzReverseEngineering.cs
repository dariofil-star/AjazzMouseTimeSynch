using System.Reflection;
using HidSharp;

public enum MouseConnectionState
{
    Disconnected,
    Dock,
    DirectUsb
}

public enum MousePowerState
{
    Unknown,
    Awake,
    Idle,
    Sleeping,
    Charging,
    FullyCharged
}

public enum MouseActivityState
{
    Unknown,
    AwakeAndMoving,
    IdleButAwake,
    ActualSleep,
    WakeAfterMovement
}

public enum ChargingSource
{
    None,
    Dock,
    Usb
}

public enum HidReportDirection
{
    Input,
    Output,
    Feature
}

public enum HidFieldReportType
{
    Input,
    Output,
    Feature
}

public sealed record HidReportDefinition(
    int InterfaceNumber,
    int Endpoint,
    int ReportId,
    HidFieldReportType ReportType,
    HidReportDirection Direction,
    ushort UsagePage,
    ushort Usage,
    int BitOffset,
    int BitLength,
    int LogicalMinimum,
    int LogicalMaximum,
    int ReportByteLength,
    string Notes);

public sealed record HidInterfaceDescriptorSnapshot(
    string DevicePath,
    int InterfaceNumber,
    int Endpoint,
    ushort VendorId,
    ushort ProductId,
    byte[] RawReportDescriptor,
    IReadOnlyList<HidReportDefinition> Definitions,
    DateTimeOffset TimestampUtc);

public sealed record DecodedHidUsage(
    ushort UsagePage,
    ushort Usage,
    long Value,
    int BitOffset,
    int BitLength,
    bool IsKnown,
    string Notes);

public sealed record HidObservedReport(
    DateTimeOffset TimestampUtc,
    string DevicePath,
    string DeviceIdentityKey,
    MouseConnectionState Connection,
    int InterfaceNumber,
    int Endpoint,
    HidReportDirection Direction,
    int ReportId,
    int Length,
    byte[] RawBytes,
    IReadOnlyList<DecodedHidUsage> DecodedUsages,
    bool IsMovement,
    bool IsButton,
    bool IsWheel,
    bool IsVendor,
    string Notes);

public sealed record ReverseEngineeringEvidence(
    string Kind,
    bool Observed,
    double Confidence,
    string Details,
    DateTimeOffset TimestampUtc);

public sealed record MouseState(
    MouseConnectionState ConnectionMode,
    MouseActivityState ActivityState,
    MousePowerState PowerState,
    ChargingSource ChargingSource,
    int? BatteryPercent,
    bool IsMoving,
    bool IsSleeping,
    bool IsCharging,
    bool IsFullyCharged,
    DateTimeOffset? LastActivity,
    DateTimeOffset? LastHidReport,
    DateTimeOffset? LastBatteryUpdate,
    string DerivedState,
    double Confidence,
    IReadOnlyList<ReverseEngineeringEvidence> Evidence);

public sealed record MouseStateTransition(
    MouseState Previous,
    MouseState Current,
    DateTimeOffset TimestampUtc,
    string Reason,
    IReadOnlyList<ReverseEngineeringEvidence> Evidence);

public sealed record CaptureSample(
    DateTimeOffset TimestampUtc,
    int ReportId,
    int InterfaceNumber,
    int Endpoint,
    HidReportDirection Direction,
    byte[] RawBytes);

public sealed record LabeledCapture(
    string Label,
    DateTimeOffset StartedUtc,
    DateTimeOffset EndedUtc,
    IReadOnlyList<CaptureSample> Samples);

public sealed record CaptureByteDifference(
    int ByteIndex,
    int? From,
    int? To,
    bool Changed);

public sealed record CaptureDiffResult(
    string LeftLabel,
    string RightLabel,
    int ReportId,
    int InterfaceNumber,
    int Endpoint,
    IReadOnlyList<CaptureByteDifference> Differences,
    int ChangedBytes,
    int SampleCount);

public sealed class HidMouseReverseEngineeringEngine
{
    private readonly Lock _lock = new();

    private const int MaxRecentReports = 5000;

    private readonly Dictionary<string, HidInterfaceDescriptorSnapshot> _descriptorSnapshots = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<HidObservedReport> _recentReports = [];
    private readonly Dictionary<string, LinkedList<CaptureSample>> _captureBuffers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LabeledCapture> _completedCaptures = new(StringComparer.OrdinalIgnoreCase);

    private DateTimeOffset? _lastAnyHidReportUtc;
    private DateTimeOffset? _lastInputReportUtc;
    private DateTimeOffset? _lastMovementUtc;
    private DateTimeOffset? _lastButtonUtc;
    private DateTimeOffset? _lastBatteryUtc;
    private int? _lastBatteryPercent;
    private byte? _lastVendorChargingByte;
    private DateTimeOffset? _wakeUntilUtc;
    private readonly List<double> _observedSleepTimeoutsSeconds = [];

    private MouseState _state = new(
        MouseConnectionState.Disconnected,
        MouseActivityState.Unknown,
        MousePowerState.Unknown,
        ChargingSource.None,
        null,
        false,
        false,
        false,
        false,
        null,
        null,
        null,
        "disconnected",
        0,
        []);

    public event EventHandler<MouseStateTransition>? StateChanged;
    public event EventHandler<MouseState>? BatteryChanged;
    public event EventHandler<MouseState>? ChargingChanged;
    public event EventHandler<MouseState>? ActivityChanged;
    public event EventHandler<MouseState>? ConnectionChanged;
    public event EventHandler<MouseState>? SleepChanged;
    public event EventHandler<MouseState>? WakeDetected;

    public MouseState GetState()
    {
        lock (_lock)
        {
            return _state;
        }
    }

    public IReadOnlyList<HidObservedReport> GetRecentReports(int take)
    {
        int max = take <= 0 ? 200 : Math.Min(take, 2000);
        lock (_lock)
        {
            return _recentReports.TakeLast(max).Reverse().ToList();
        }
    }

    public IReadOnlyList<HidInterfaceDescriptorSnapshot> GetDescriptorSnapshots()
    {
        lock (_lock)
        {
            return _descriptorSnapshots.Values
                .OrderBy(v => v.InterfaceNumber)
                .ThenBy(v => v.DevicePath, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public IReadOnlyList<LabeledCapture> GetCompletedCaptures()
    {
        lock (_lock)
        {
            return _completedCaptures.Values
                .OrderBy(c => c.StartedUtc)
                .ToList();
        }
    }

    public void BeginCapture(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Capture label is required.", nameof(label));
        }

        lock (_lock)
        {
            _captureBuffers[label.Trim()] = [];
        }
    }

    public void EndCapture(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Capture label is required.", nameof(label));
        }

        string key = label.Trim();

        lock (_lock)
        {
            if (!_captureBuffers.TryGetValue(key, out LinkedList<CaptureSample>? samples))
            {
                return;
            }

            _captureBuffers.Remove(key);

            List<CaptureSample> ordered = samples.OrderBy(s => s.TimestampUtc).ToList();
            DateTimeOffset start = ordered.Count > 0 ? ordered[0].TimestampUtc : DateTimeOffset.UtcNow;
            DateTimeOffset end = ordered.Count > 0 ? ordered[^1].TimestampUtc : start;

            _completedCaptures[key] = new LabeledCapture(key, start, end, ordered);
        }
    }

    public CaptureDiffResult? DiffCaptures(string leftLabel, string rightLabel, int reportId, int interfaceNumber, int endpoint)
    {
        lock (_lock)
        {
            if (!_completedCaptures.TryGetValue(leftLabel, out LabeledCapture? left)
                || !_completedCaptures.TryGetValue(rightLabel, out LabeledCapture? right))
            {
                return null;
            }

            List<CaptureSample> leftSamples = left.Samples
                .Where(s => s.ReportId == reportId && s.InterfaceNumber == interfaceNumber && s.Endpoint == endpoint)
                .ToList();
            List<CaptureSample> rightSamples = right.Samples
                .Where(s => s.ReportId == reportId && s.InterfaceNumber == interfaceNumber && s.Endpoint == endpoint)
                .ToList();

            if (leftSamples.Count == 0 || rightSamples.Count == 0)
            {
                return new CaptureDiffResult(leftLabel, rightLabel, reportId, interfaceNumber, endpoint, [], 0, 0);
            }

            byte[] leftMost = leftSamples[^1].RawBytes;
            byte[] rightMost = rightSamples[^1].RawBytes;
            int len = Math.Max(leftMost.Length, rightMost.Length);
            List<CaptureByteDifference> diffs = new(len);

            for (int i = 0; i < len; i++)
            {
                int? from = i < leftMost.Length ? leftMost[i] : null;
                int? to = i < rightMost.Length ? rightMost[i] : null;
                diffs.Add(new CaptureByteDifference(i, from, to, from != to));
            }

            return new CaptureDiffResult(
                leftLabel,
                rightLabel,
                reportId,
                interfaceNumber,
                endpoint,
                diffs,
                diffs.Count(d => d.Changed),
                Math.Min(leftSamples.Count, rightSamples.Count));
        }
    }

    public void UpdateConnection(MouseConnectionState connection, string details)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<ReverseEngineeringEvidence> evidence =
        [
            new ReverseEngineeringEvidence("connection", true, 1.0, details, now)
        ];

        lock (_lock)
        {
            MouseState previous = _state;
            bool removedFromDock = previous.ConnectionMode == MouseConnectionState.Dock
                && connection != MouseConnectionState.Dock;

            if (connection == MouseConnectionState.Disconnected)
            {
                _lastAnyHidReportUtc = null;
                _lastInputReportUtc = null;
                _lastMovementUtc = null;
                _lastButtonUtc = null;
                _lastBatteryUtc = null;
                _lastBatteryPercent = null;
                _lastVendorChargingByte = null;
                _wakeUntilUtc = null;
            }

            MouseState current = _state with
            {
                ConnectionMode = connection,
                ActivityState = connection == MouseConnectionState.Disconnected ? MouseActivityState.Unknown : _state.ActivityState,
                PowerState = connection == MouseConnectionState.Disconnected ? MousePowerState.Unknown : _state.PowerState,
                ChargingSource = connection == MouseConnectionState.Disconnected ? ChargingSource.None : _state.ChargingSource,
                BatteryPercent = connection == MouseConnectionState.Disconnected ? null : _lastBatteryPercent,
                IsCharging = connection == MouseConnectionState.Disconnected ? false : _state.IsCharging,
                IsFullyCharged = connection == MouseConnectionState.Disconnected ? false : _state.IsFullyCharged,
                IsSleeping = connection == MouseConnectionState.Disconnected ? false : _state.IsSleeping,
                IsMoving = false,
                LastHidReport = _lastAnyHidReportUtc,
                LastActivity = _lastMovementUtc,
                LastBatteryUpdate = _lastBatteryUtc,
                Confidence = 1.0,
                Evidence = evidence
            };

            current = RebuildDerivedState(current, removedFromDock);
            _state = current;

            RaiseTransitions(previous, current, "Connection changed", evidence);
        }
    }

    public void Tick(DateTimeOffset now)
    {
        lock (_lock)
        {
            if (_state.ConnectionMode == MouseConnectionState.Disconnected)
            {
                return;
            }

            DateTimeOffset? referenceReport = _lastInputReportUtc ?? _lastAnyHidReportUtc;
            if (!referenceReport.HasValue)
            {
                return;
            }

            TimeSpan silence = now - referenceReport.Value;
            double sleepThreshold = GetSleepSilenceThresholdSeconds();

            if (silence.TotalSeconds < sleepThreshold)
            {
                return;
            }

            List<ReverseEngineeringEvidence> evidence =
            [
                new ReverseEngineeringEvidence("sleep", false, 0.82, $"No HID reports for {silence.TotalSeconds:F1}s; threshold {sleepThreshold:F1}s.", now)
            ];

            MouseState previous = _state;
            MouseState current = _state with
            {
                ActivityState = MouseActivityState.ActualSleep,
                PowerState = _state.IsCharging ? MousePowerState.Charging : MousePowerState.Sleeping,
                IsSleeping = true,
                IsMoving = false,
                Confidence = 0.82,
                Evidence = evidence
            };

            current = RebuildDerivedState(current, removedFromDock: false);
            _state = current;
            RaiseTransitions(previous, current, "Sleep inferred from HID silence", evidence);
        }
    }

    public void RecordDescriptor(HidDevice device, int interfaceNumber, int endpoint)
    {
        byte[]? raw = TryGetRawReportDescriptor(device);
        if (raw is null || raw.Length == 0)
        {
            return;
        }

        List<HidReportDefinition> definitions = ParseReportDefinitions(raw, interfaceNumber, endpoint);
        string descriptorKey = $"{device.DevicePath}|mi_{interfaceNumber:00}";

        lock (_lock)
        {
            _descriptorSnapshots[descriptorKey] = new HidInterfaceDescriptorSnapshot(
                device.DevicePath,
                interfaceNumber,
                endpoint,
                (ushort)device.VendorID,
                (ushort)device.ProductID,
                raw,
                definitions,
                DateTimeOffset.UtcNow);
        }
    }

    public void RecordReport(HidObservedReport report)
    {
        DateTimeOffset now = report.TimestampUtc;

        lock (_lock)
        {
            _recentReports.AddLast(report);
            while (_recentReports.Count > MaxRecentReports)
            {
                _recentReports.RemoveFirst();
            }

            foreach (LinkedList<CaptureSample> buffer in _captureBuffers.Values)
            {
                buffer.AddLast(new CaptureSample(report.TimestampUtc, report.ReportId, report.InterfaceNumber, report.Endpoint, report.Direction, report.RawBytes));
            }

            MouseState previous = _state;

            _lastAnyHidReportUtc = now;
            if (report.Direction == HidReportDirection.Input)
            {
                _lastInputReportUtc = now;
            }

            if (report.IsMovement || report.IsWheel)
            {
                _lastMovementUtc = now;
            }

            if (report.IsButton)
            {
                _lastButtonUtc = now;
            }

            int? batteryCandidate = TryExtractBatteryCandidate(report);
            if (batteryCandidate.HasValue)
            {
                _lastBatteryPercent = batteryCandidate;
                _lastBatteryUtc = now;
            }

            bool chargingCandidate = TryExtractChargingCandidate(report, out byte? chargingByte);
            bool charging = false;
            bool fullyCharged = false;

            if (chargingCandidate && chargingByte.HasValue)
            {
                charging = chargingByte.Value == 0x01;
                fullyCharged = chargingByte.Value == 0x02;
                _lastVendorChargingByte = chargingByte.Value;
            }

            if (_lastBatteryPercent.HasValue && previous.BatteryPercent.HasValue && _lastBatteryPercent.Value > previous.BatteryPercent.Value)
            {
                charging = true;
            }

            if (_lastBatteryPercent.HasValue && _lastBatteryPercent.Value >= 100 && charging)
            {
                fullyCharged = true;
                charging = false;
            }

            MouseActivityState activity = ComputeActivityState(now, report.IsMovement || report.IsWheel || report.IsButton);
            MousePowerState power = ComputePowerState(activity, charging, fullyCharged);

            List<ReverseEngineeringEvidence> evidence = BuildEvidence(report, batteryCandidate, chargingCandidate, chargingByte, activity, charging, fullyCharged);

            MouseState current = _state with
            {
                ActivityState = activity,
                PowerState = power,
                ChargingSource = ResolveChargingSource(charging || fullyCharged),
                BatteryPercent = _lastBatteryPercent,
                IsMoving = report.IsMovement || report.IsWheel || report.IsButton,
                IsSleeping = activity == MouseActivityState.ActualSleep,
                IsCharging = charging,
                IsFullyCharged = fullyCharged,
                LastActivity = _lastMovementUtc,
                LastHidReport = _lastAnyHidReportUtc,
                LastBatteryUpdate = _lastBatteryUtc,
                Confidence = evidence.Count == 0 ? 0.5 : evidence.Average(e => e.Confidence),
                Evidence = evidence
            };

            current = RebuildDerivedState(current, removedFromDock: false);
            _state = current;
            RaiseTransitions(previous, current, "HID report observed", evidence);
        }
    }

    private static byte[]? TryGetRawReportDescriptor(HidDevice device)
    {
        object target = device;

        foreach (string methodName in new[] { "GetRawReportDescriptor", "GetReportDescriptor" })
        {
            MethodInfo? method = target.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance, binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method is null)
            {
                continue;
            }

            object? value;
            try
            {
                value = method.Invoke(target, null);
            }
            catch
            {
                continue;
            }

            if (value is byte[] bytes)
            {
                return bytes;
            }

            if (value is null)
            {
                continue;
            }

            MethodInfo? nested = value.GetType().GetMethod("GetRawReportDescriptor", BindingFlags.Public | BindingFlags.Instance, binder: null, types: Type.EmptyTypes, modifiers: null);
            if (nested is not null)
            {
                try
                {
                    if (nested.Invoke(value, null) is byte[] nestedBytes)
                    {
                        return nestedBytes;
                    }
                }
                catch
                {
                    // ignored
                }
            }
        }

        return null;
    }

    private static List<HidReportDefinition> ParseReportDefinitions(byte[] rawDescriptor, int interfaceNumber, int endpoint)
    {
        List<HidReportDefinition> definitions = [];
        Dictionary<(HidFieldReportType Type, int ReportId), int> reportBitOffsets = new();

        int reportId = 0;
        ushort usagePage = 0;
        int logicalMinimum = 0;
        int logicalMaximum = 0;
        int reportSize = 0;
        int reportCount = 0;

        List<ushort> usages = [];
        ushort? usageMinimum = null;
        ushort? usageMaximum = null;

        int index = 0;
        while (index < rawDescriptor.Length)
        {
            byte prefix = rawDescriptor[index++];
            if (prefix == 0xFE)
            {
                if (index + 1 >= rawDescriptor.Length)
                {
                    break;
                }

                int dataSize = rawDescriptor[index];
                index += 2 + dataSize;
                continue;
            }

            int sizeCode = prefix & 0x03;
            int dataLength = sizeCode == 3 ? 4 : sizeCode;
            int itemType = (prefix >> 2) & 0x03;
            int itemTag = (prefix >> 4) & 0x0F;

            if (index + dataLength > rawDescriptor.Length)
            {
                break;
            }

            ReadOnlySpan<byte> data = rawDescriptor.AsSpan(index, dataLength);
            index += dataLength;

            if (itemType == 1)
            {
                switch (itemTag)
                {
                    case 0x0:
                        usagePage = (ushort)ReadUnsigned(data);
                        break;
                    case 0x1:
                        logicalMinimum = ReadSigned(data);
                        break;
                    case 0x2:
                        logicalMaximum = ReadSigned(data);
                        break;
                    case 0x7:
                        reportSize = ReadUnsigned(data);
                        break;
                    case 0x8:
                        reportId = ReadUnsigned(data);
                        break;
                    case 0x9:
                        reportCount = ReadUnsigned(data);
                        break;
                }

                continue;
            }

            if (itemType == 2)
            {
                switch (itemTag)
                {
                    case 0x0:
                        usages.Add((ushort)ReadUnsigned(data));
                        break;
                    case 0x1:
                        usageMinimum = (ushort)ReadUnsigned(data);
                        break;
                    case 0x2:
                        usageMaximum = (ushort)ReadUnsigned(data);
                        break;
                }

                continue;
            }

            if (itemType != 0)
            {
                continue;
            }

            HidFieldReportType? reportType = itemTag switch
            {
                0x8 => HidFieldReportType.Input,
                0x9 => HidFieldReportType.Output,
                0xB => HidFieldReportType.Feature,
                _ => null
            };

            if (!reportType.HasValue || reportCount <= 0 || reportSize <= 0)
            {
                usages.Clear();
                usageMinimum = null;
                usageMaximum = null;
                continue;
            }

            (HidFieldReportType Type, int ReportId) key = (reportType.Value, reportId);
            if (!reportBitOffsets.TryGetValue(key, out int currentBitOffset))
            {
                currentBitOffset = 0;
            }

            bool isConstant = dataLength > 0 && (data[0] & 0x01) == 0x01;

            for (int i = 0; i < reportCount; i++)
            {
                ushort usage = ResolveUsage(usages, usageMinimum, usageMaximum, i);
                int bitOffset = currentBitOffset + (i * reportSize);
                string notes = BuildDefinitionNotes(usagePage, usage, isConstant, reportType.Value, endpoint);

                definitions.Add(new HidReportDefinition(
                    interfaceNumber,
                    endpoint,
                    reportId,
                    reportType.Value,
                    MapDirection(reportType.Value),
                    usagePage,
                    usage,
                    bitOffset,
                    reportSize,
                    logicalMinimum,
                    logicalMaximum,
                    0,
                    notes));
            }

            currentBitOffset += reportCount * reportSize;
            reportBitOffsets[key] = currentBitOffset;

            usages.Clear();
            usageMinimum = null;
            usageMaximum = null;
        }

        if (definitions.Count == 0)
        {
            return definitions;
        }

        var reportLengths = definitions
            .GroupBy(d => (d.ReportType, d.ReportId))
            .ToDictionary(
                g => g.Key,
                g => (int)Math.Ceiling(g.Max(v => v.BitOffset + v.BitLength) / 8.0));

        return definitions
            .Select(d => d with { ReportByteLength = reportLengths[(d.ReportType, d.ReportId)] + (d.ReportId > 0 ? 1 : 0) })
            .ToList();
    }

    private static HidReportDirection MapDirection(HidFieldReportType type)
    {
        return type switch
        {
            HidFieldReportType.Input => HidReportDirection.Input,
            HidFieldReportType.Output => HidReportDirection.Output,
            _ => HidReportDirection.Feature
        };
    }

    private static ushort ResolveUsage(List<ushort> usages, ushort? usageMinimum, ushort? usageMaximum, int index)
    {
        if (index < usages.Count)
        {
            return usages[index];
        }

        if (usageMinimum.HasValue && usageMaximum.HasValue)
        {
            int resolved = usageMinimum.Value + index;
            if (resolved <= usageMaximum.Value)
            {
                return (ushort)resolved;
            }

            return usageMaximum.Value;
        }

        return 0;
    }

    private static string BuildDefinitionNotes(ushort usagePage, ushort usage, bool isConstant, HidFieldReportType reportType, int endpoint)
    {
        if (usagePage == 0xFFFF)
        {
            return reportType == HidFieldReportType.Feature
                ? $"vendor feature endpoint 0x{endpoint:X2}"
                : "vendor-defined usage";
        }

        if (usagePage == 0x01 && usage == 0x30)
        {
            return "X axis";
        }

        if (usagePage == 0x01 && usage == 0x31)
        {
            return "Y axis";
        }

        if (usagePage == 0x01 && usage == 0x38)
        {
            return "wheel";
        }

        if (usagePage == 0x09)
        {
            return "button";
        }

        return isConstant ? "constant/padding" : "generic field";
    }

    private static int ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        int value = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            value |= bytes[i] << (i * 8);
        }

        return value;
    }

    private static int ReadSigned(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length == 0)
        {
            return 0;
        }

        int unsigned = ReadUnsigned(bytes);
        int shift = (4 - bytes.Length) * 8;
        return (unsigned << shift) >> shift;
    }

    private MouseActivityState ComputeActivityState(DateTimeOffset now, bool hasInteraction)
    {
        if (_state.ConnectionMode == MouseConnectionState.Disconnected)
        {
            return MouseActivityState.Unknown;
        }

        if (hasInteraction)
        {
            if (_state.ActivityState == MouseActivityState.ActualSleep)
            {
                _wakeUntilUtc = now.AddSeconds(2);
                if (_lastInputReportUtc.HasValue && _lastMovementUtc.HasValue)
                {
                    _observedSleepTimeoutsSeconds.Add((_lastMovementUtc.Value - _lastInputReportUtc.Value).Duration().TotalSeconds);
                }

                return MouseActivityState.WakeAfterMovement;
            }

            return MouseActivityState.AwakeAndMoving;
        }

        if (_wakeUntilUtc.HasValue && _wakeUntilUtc.Value > now)
        {
            return MouseActivityState.WakeAfterMovement;
        }

        DateTimeOffset? referenceInput = _lastInputReportUtc ?? _lastAnyHidReportUtc;
        if (!referenceInput.HasValue)
        {
            return MouseActivityState.Unknown;
        }

        double sinceInput = (now - referenceInput.Value).TotalSeconds;
        if (sinceInput >= GetSleepSilenceThresholdSeconds())
        {
            return MouseActivityState.ActualSleep;
        }

        if (_lastMovementUtc.HasValue && (now - _lastMovementUtc.Value).TotalSeconds <= 2.5)
        {
            return MouseActivityState.AwakeAndMoving;
        }

        return MouseActivityState.IdleButAwake;
    }

    private double GetSleepSilenceThresholdSeconds()
    {
        if (_observedSleepTimeoutsSeconds.Count == 0)
        {
            return 45;
        }

        List<double> sorted = _observedSleepTimeoutsSeconds.Where(s => s > 0.5 && s < 1200).OrderBy(s => s).ToList();
        if (sorted.Count == 0)
        {
            return 45;
        }

        int mid = sorted.Count / 2;
        return sorted[mid];
    }

    private static int? TryExtractBatteryCandidate(HidObservedReport report)
    {
        if (!report.IsVendor || report.RawBytes.Length == 0)
        {
            return null;
        }

        if (report.InterfaceNumber == 2 && report.Direction == HidReportDirection.Feature)
        {
            if (report.RawBytes.Length >= 4
                && report.RawBytes[0] == 0x05
                && report.RawBytes[1] == 0x00
                && report.RawBytes[2] == 0x00)
            {
                int percent = report.RawBytes[3];
                if (percent is >= 1 and <= 100)
                {
                    return percent;
                }
            }

            return null;
        }

        if (report.InterfaceNumber == 1 && report.ReportId == 5 && report.RawBytes.Length >= 4)
        {
            int value = report.RawBytes[3];
            if (value is >= 1 and <= 100)
            {
                return value;
            }
        }

        return null;
    }

    private static bool TryExtractChargingCandidate(HidObservedReport report, out byte? candidate)
    {
        candidate = null;

        if (!report.IsVendor || report.RawBytes.Length == 0)
        {
            return false;
        }

        if (report.InterfaceNumber == 1 && report.ReportId == 5 && report.RawBytes.Length >= 2)
        {
            candidate = report.RawBytes[1];
            return true;
        }

        if (report.InterfaceNumber == 2 && report.Direction == HidReportDirection.Feature && report.RawBytes.Length >= 2)
        {
            candidate = report.RawBytes[1];
            return true;
        }

        return false;
    }

    private MousePowerState ComputePowerState(MouseActivityState activity, bool charging, bool full)
    {
        if (full)
        {
            return MousePowerState.FullyCharged;
        }

        if (charging)
        {
            return MousePowerState.Charging;
        }

        return activity switch
        {
            MouseActivityState.AwakeAndMoving or MouseActivityState.WakeAfterMovement => MousePowerState.Awake,
            MouseActivityState.IdleButAwake => MousePowerState.Idle,
            MouseActivityState.ActualSleep => MousePowerState.Sleeping,
            _ => MousePowerState.Unknown
        };
    }

    private ChargingSource ResolveChargingSource(bool chargingOrFull)
    {
        if (!chargingOrFull)
        {
            return ChargingSource.None;
        }

        return _state.ConnectionMode switch
        {
            MouseConnectionState.Dock => ChargingSource.Dock,
            MouseConnectionState.DirectUsb => ChargingSource.Usb,
            _ => ChargingSource.None
        };
    }

    private List<ReverseEngineeringEvidence> BuildEvidence(
        HidObservedReport report,
        int? batteryCandidate,
        bool chargingCandidate,
        byte? chargingByte,
        MouseActivityState activity,
        bool charging,
        bool fullyCharged)
    {
        List<ReverseEngineeringEvidence> evidence =
        [
            new ReverseEngineeringEvidence(
                "hid-report",
                true,
                1.0,
                $"Interface mi_{report.InterfaceNumber:00}, endpoint 0x{report.Endpoint:X2}, reportId {report.ReportId}, length {report.Length}.",
                report.TimestampUtc)
        ];

        if (report.IsMovement || report.IsWheel || report.IsButton)
        {
            evidence.Add(new ReverseEngineeringEvidence("activity", true, 0.99, "Movement/button/wheel observed.", report.TimestampUtc));
        }

        if (activity == MouseActivityState.ActualSleep)
        {
            evidence.Add(new ReverseEngineeringEvidence("sleep", false, 0.82, "Inferred from HID silence timeout.", report.TimestampUtc));
        }

        if (batteryCandidate.HasValue)
        {
            evidence.Add(new ReverseEngineeringEvidence("battery-candidate", false, 0.45, $"Vendor field candidate value {batteryCandidate.Value} in 0..100 range.", report.TimestampUtc));
        }

        if (chargingCandidate && chargingByte.HasValue)
        {
            evidence.Add(new ReverseEngineeringEvidence("charging-candidate", false, 0.40, $"Vendor byte candidate {chargingByte.Value}.", report.TimestampUtc));
        }

        if (charging)
        {
            evidence.Add(new ReverseEngineeringEvidence("charging", false, 0.58, "Charging inferred from vendor candidate change or battery rise.", report.TimestampUtc));
        }

        if (fullyCharged)
        {
            evidence.Add(new ReverseEngineeringEvidence("full", false, 0.62, "Fully charged inferred from battery candidate >= 100 or vendor full flag pattern.", report.TimestampUtc));
        }

        return evidence;
    }

    private static MouseState RebuildDerivedState(MouseState state, bool removedFromDock)
    {
        if (state.ConnectionMode == MouseConnectionState.Disconnected)
        {
            return state with { DerivedState = "disconnected" };
        }

        if (removedFromDock)
        {
            return state with { DerivedState = "removed-from-dock" };
        }

        if (state.ConnectionMode == MouseConnectionState.DirectUsb)
        {
            if (state.IsFullyCharged)
            {
                return state with { DerivedState = "fully-charged-on-usb" };
            }

            if (state.IsCharging)
            {
                return state with { DerivedState = "usb-cable-charging" };
            }

            return state with { DerivedState = "usb-cable-connected" };
        }

        if (state.ConnectionMode == MouseConnectionState.Dock)
        {
            if (state.IsFullyCharged)
            {
                return state with { DerivedState = "fully-charged-on-dock" };
            }

            if (state.IsCharging)
            {
                return state with { DerivedState = "charging-on-dock" };
            }

            return state with { DerivedState = "placed-on-dock" };
        }

        return state.ActivityState switch
        {
            MouseActivityState.ActualSleep => state with { DerivedState = "sleeping-off-dock" },
            MouseActivityState.IdleButAwake => state with { DerivedState = "idle-off-dock" },
            _ => state with { DerivedState = "awake-off-dock" }
        };
    }

    private void RaiseTransitions(MouseState previous, MouseState current, string reason, IReadOnlyList<ReverseEngineeringEvidence> evidence)
    {
        StateChanged?.Invoke(this, new MouseStateTransition(previous, current, DateTimeOffset.UtcNow, reason, evidence));

        if (previous.ConnectionMode != current.ConnectionMode)
        {
            ConnectionChanged?.Invoke(this, current);
        }

        if (previous.ActivityState != current.ActivityState)
        {
            ActivityChanged?.Invoke(this, current);
        }

        if (previous.IsCharging != current.IsCharging)
        {
            ChargingChanged?.Invoke(this, current);
        }

        if (previous.BatteryPercent != current.BatteryPercent)
        {
            BatteryChanged?.Invoke(this, current);
        }

        if (!previous.IsSleeping && current.IsSleeping)
        {
            SleepChanged?.Invoke(this, current);
        }

        if (previous.ActivityState == MouseActivityState.ActualSleep && current.ActivityState == MouseActivityState.WakeAfterMovement)
        {
            WakeDetected?.Invoke(this, current);
        }
    }
}
