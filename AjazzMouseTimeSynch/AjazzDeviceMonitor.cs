using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

public enum AjazzConnectionMode
{
    Unknown,
    Disconnected,
    Dock,
    Direct
}

public sealed record AjazzHidInterfaceInfo(
    string DeviceInterfacePath,
    string DeviceInstanceId,
    ushort VendorId,
    ushort ProductId,
    AjazzConnectionMode Mode,
    string Manufacturer,
    string Product,
    bool IsInputInterface,
    bool IsControlInterface);

public sealed record AjazzDeviceSnapshot(
    DateTimeOffset TimestampUtc,
    AjazzConnectionMode Mode,
    string DeviceInterfacePath,
    string DeviceInstanceId,
    string InputInterfacePath,
    string ControlInterfacePath,
    ushort VendorId,
    ushort ProductId,
    string Manufacturer,
    string Product,
    IReadOnlyList<AjazzHidInterfaceInfo> Interfaces)
{
    public static AjazzDeviceSnapshot Disconnected { get; } = new(
        DateTimeOffset.UtcNow,
        AjazzConnectionMode.Disconnected,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        0,
        0,
        string.Empty,
        string.Empty,
        Array.Empty<AjazzHidInterfaceInfo>());
}

public sealed class AjazzDeviceMonitor : IDisposable
{
    private const ushort TargetVendorId = 0x3151;
    private const ushort DockProductId = 0x5007;
    private const ushort DirectProductId = 0x4026;

    private static readonly Guid HidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
    private static readonly Guid UsbDeviceInterfaceGuid = new("A5DCBF10-6530-11D2-901F-00C04FB951ED");
    private static readonly Regex VidPidRegex = new(@"vid_([0-9a-f]{4}).*pid_([0-9a-f]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MultiInterfaceSuffixRegex = new(@"&mi_[0-9a-f]{2}", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Lock _lock = new();
    private readonly TimeSpan _debounceDelay;
    private readonly Timer _debounceTimer;
    private readonly CmNotifyCallback _callback;

    private IntPtr _hidNotificationHandle;
    private IntPtr _usbNotificationHandle;
    private bool _started;
    private bool _disposed;

    public AjazzDeviceMonitor(TimeSpan? debounceDelay = null)
    {
        _debounceDelay = debounceDelay ?? TimeSpan.FromMilliseconds(750);
        _debounceTimer = new Timer(_ => RefreshNow(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _callback = OnDeviceNotification;
    }

    public event EventHandler<AjazzDeviceSnapshot>? SnapshotChanged;

    public AjazzDeviceSnapshot CurrentSnapshot { get; private set; } = AjazzDeviceSnapshot.Disconnected;

    public void Start()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            if (_started)
            {
                return;
            }

            RegisterNotification(HidInterfaceGuid, out _hidNotificationHandle);
            RegisterNotification(UsbDeviceInterfaceGuid, out _usbNotificationHandle);
            _started = true;
        }

        RefreshNow();
    }

    public void RefreshSoon()
    {
        ThrowIfDisposed();

        lock (_lock)
        {
            if (!_started)
            {
                return;
            }

            _debounceTimer.Change(_debounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        lock (_lock)
        {
            if (_hidNotificationHandle != IntPtr.Zero)
            {
                CM_Unregister_Notification(_hidNotificationHandle);
                _hidNotificationHandle = IntPtr.Zero;
            }

            if (_usbNotificationHandle != IntPtr.Zero)
            {
                CM_Unregister_Notification(_usbNotificationHandle);
                _usbNotificationHandle = IntPtr.Zero;
            }

            _started = false;
            _disposed = true;
        }

        _debounceTimer.Dispose();
    }

    private uint OnDeviceNotification(IntPtr notifyHandle, IntPtr context, CmNotifyAction action, IntPtr eventData, uint eventDataSize)
    {
        if (_disposed)
        {
            return 0;
        }

        RefreshSoon();
        return 0;
    }

    private void RefreshNow()
    {
        if (_disposed)
        {
            return;
        }

        AjazzDeviceSnapshot snapshot = EnumerateSnapshot();
        CurrentSnapshot = snapshot;
        SnapshotChanged?.Invoke(this, snapshot);
    }

    private void RegisterNotification(Guid interfaceGuid, out IntPtr notificationHandle)
    {
        CmNotifyFilter filter = new()
        {
            cbSize = (uint)Marshal.SizeOf<CmNotifyFilter>(),
            FilterType = CmNotifyFilterType.DeviceInterface,
            ClassGuid = interfaceGuid
        };

        int result = CM_Register_Notification(in filter, IntPtr.Zero, _callback, out notificationHandle);
        if (result != 0)
        {
            throw new Win32Exception(result, $"CM_Register_Notification failed for {interfaceGuid}.");
        }
    }

    private static AjazzDeviceSnapshot EnumerateSnapshot()
    {
        List<AjazzHidInterfaceInfo> interfaces = EnumerateAjazzInterfaces();
        if (interfaces.Count == 0)
        {
            return AjazzDeviceSnapshot.Disconnected with { TimestampUtc = DateTimeOffset.UtcNow };
        }

        var groups = interfaces
            .GroupBy(i => BuildGroupKey(i.DeviceInstanceId, i.DeviceInterfacePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => new
            {
                Interfaces = g.OrderByDescending(i => i.IsControlInterface)
                    .ThenByDescending(i => i.IsInputInterface)
                    .ThenBy(i => i.DeviceInterfacePath, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Score = g.Count()
                    + (g.Any(i => i.IsControlInterface) ? 10 : 0)
                    + (g.Any(i => i.IsInputInterface) ? 5 : 0)
            })
            .OrderByDescending(g => g.Score)
            .ThenByDescending(g => g.Interfaces.Any(i => i.Mode == AjazzConnectionMode.Direct))
            .ToList();

        List<AjazzHidInterfaceInfo> selectedGroup = groups[0].Interfaces;
        AjazzHidInterfaceInfo primary = selectedGroup.First();
        AjazzHidInterfaceInfo? input = selectedGroup.FirstOrDefault(i => i.IsInputInterface);
        AjazzHidInterfaceInfo? control = selectedGroup.FirstOrDefault(i => i.IsControlInterface);

        return new AjazzDeviceSnapshot(
            DateTimeOffset.UtcNow,
            primary.Mode,
            primary.DeviceInterfacePath,
            primary.DeviceInstanceId,
            input?.DeviceInterfacePath ?? string.Empty,
            control?.DeviceInterfacePath ?? string.Empty,
            primary.VendorId,
            primary.ProductId,
            primary.Manufacturer,
            primary.Product,
            new ReadOnlyCollection<AjazzHidInterfaceInfo>(selectedGroup));
    }

    private static string BuildGroupKey(string deviceInstanceId, string deviceInterfacePath)
    {
        string source = !string.IsNullOrWhiteSpace(deviceInstanceId) ? deviceInstanceId : deviceInterfacePath;
        return MultiInterfaceSuffixRegex.Replace(source, string.Empty);
    }

    private static List<AjazzHidInterfaceInfo> EnumerateAjazzInterfaces()
    {
        Guid hidInterfaceGuid = new("4D1E55B2-F16F-11CF-88CB-001111000030");
        IntPtr deviceInfoSet = SetupDiGetClassDevs(ref hidInterfaceGuid, null, IntPtr.Zero, DiGetClassFlags.Present | DiGetClassFlags.DeviceInterface);
        if (deviceInfoSet == InvalidHandleValue)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetClassDevs failed for HID devices.");
        }

        try
        {
            List<AjazzHidInterfaceInfo> devices = [];
            uint index = 0;

            while (true)
            {
                SpDeviceInterfaceData interfaceData = new()
                {
                    cbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>()
                };

                if (!SetupDiEnumDeviceInterfaces(deviceInfoSet, IntPtr.Zero, ref hidInterfaceGuid, index, ref interfaceData))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error == ErrorNoMoreItems)
                    {
                        break;
                    }

                    throw new Win32Exception(error, "SetupDiEnumDeviceInterfaces failed.");
                }

                devices.Add(ReadInterfaceInfo(deviceInfoSet, interfaceData));
                index++;
            }

            return devices
                .Where(d => d.VendorId == TargetVendorId && (d.ProductId == DockProductId || d.ProductId == DirectProductId))
                .ToList();
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(deviceInfoSet);
        }
    }

    private static AjazzHidInterfaceInfo ReadInterfaceInfo(IntPtr deviceInfoSet, SpDeviceInterfaceData interfaceData)
    {
        SpDevinfoData devinfoData = new()
        {
            cbSize = (uint)Marshal.SizeOf<SpDevinfoData>()
        };

        _ = SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, IntPtr.Zero, 0, out uint requiredSize, ref devinfoData);
        int expectedError = Marshal.GetLastWin32Error();
        if (requiredSize == 0 || (expectedError != ErrorInsufficientBuffer && expectedError != 0))
        {
            throw new Win32Exception(expectedError, "SetupDiGetDeviceInterfaceDetail sizing call failed.");
        }

        IntPtr detailBuffer = Marshal.AllocHGlobal((int)requiredSize);
        try
        {
            Marshal.WriteInt32(detailBuffer, IntPtr.Size == 8 ? 8 : 6);

            if (!SetupDiGetDeviceInterfaceDetail(deviceInfoSet, ref interfaceData, detailBuffer, requiredSize, out _, ref devinfoData))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInterfaceDetail failed.");
            }

            string devicePath = Marshal.PtrToStringUni(IntPtr.Add(detailBuffer, 4)) ?? string.Empty;
            string instanceId = GetDeviceInstanceId(deviceInfoSet, devinfoData);
            string hardwareIds = GetDeviceRegistryProperty(deviceInfoSet, devinfoData, SetupDiRegistryProperty.HardwareId);
            string manufacturer = GetDeviceRegistryProperty(deviceInfoSet, devinfoData, SetupDiRegistryProperty.Manufacturer);
            string product = GetDeviceRegistryProperty(deviceInfoSet, devinfoData, SetupDiRegistryProperty.FriendlyName);

            if (string.IsNullOrWhiteSpace(product))
            {
                product = GetDeviceRegistryProperty(deviceInfoSet, devinfoData, SetupDiRegistryProperty.DeviceDescription);
            }

            (ushort vendorId, ushort productId) = ParseVidPid(devicePath, hardwareIds);
            AjazzConnectionMode mode = productId switch
            {
                DockProductId => AjazzConnectionMode.Dock,
                DirectProductId => AjazzConnectionMode.Direct,
                _ => AjazzConnectionMode.Unknown
            };

            return new AjazzHidInterfaceInfo(
                devicePath,
                instanceId,
                vendorId,
                productId,
                mode,
                manufacturer,
                product,
                IsInputInterface(devicePath),
                IsControlInterface(devicePath));
        }
        finally
        {
            Marshal.FreeHGlobal(detailBuffer);
        }
    }

    private static (ushort VendorId, ushort ProductId) ParseVidPid(string devicePath, string hardwareIds)
    {
        string source = string.IsNullOrWhiteSpace(hardwareIds) ? devicePath : $"{devicePath}|{hardwareIds}";
        Match match = VidPidRegex.Match(source);
        if (!match.Success)
        {
            return (0, 0);
        }

        ushort vendorId = Convert.ToUInt16(match.Groups[1].Value, 16);
        ushort productId = Convert.ToUInt16(match.Groups[2].Value, 16);
        return (vendorId, productId);
    }

    private static string GetDeviceInstanceId(IntPtr deviceInfoSet, SpDevinfoData devinfoData)
    {
        StringBuilder builder = new(256);
        if (SetupDiGetDeviceInstanceId(deviceInfoSet, ref devinfoData, builder, builder.Capacity, out _))
        {
            return builder.ToString();
        }

        throw new Win32Exception(Marshal.GetLastWin32Error(), "SetupDiGetDeviceInstanceId failed.");
    }

    private static string GetDeviceRegistryProperty(IntPtr deviceInfoSet, SpDevinfoData devinfoData, SetupDiRegistryProperty property)
    {
        byte[] buffer = new byte[1024];
        if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devinfoData, property, out _, buffer, (uint)buffer.Length, out uint requiredSize))
        {
            int error = Marshal.GetLastWin32Error();
            if (error == ErrorInvalidData || error == ErrorNotFound)
            {
                return string.Empty;
            }

            if (error != ErrorInsufficientBuffer)
            {
                throw new Win32Exception(error, $"SetupDiGetDeviceRegistryProperty failed for {property}.");
            }

            buffer = new byte[requiredSize];
            if (!SetupDiGetDeviceRegistryProperty(deviceInfoSet, ref devinfoData, property, out _, buffer, (uint)buffer.Length, out _))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"SetupDiGetDeviceRegistryProperty retry failed for {property}.");
            }
        }

        return DecodeRegistryString(buffer);
    }

    private static string DecodeRegistryString(byte[] buffer)
    {
        string value = Encoding.Unicode.GetString(buffer);
        int nullIndex = value.IndexOf('\0');
        return nullIndex >= 0 ? value[..nullIndex].Trim() : value.Trim();
    }

    private static bool IsInputInterface(string devicePath)
    {
        return devicePath.Contains("&mi_00", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControlInterface(string devicePath)
    {
        return devicePath.Contains("&mi_02", StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorInvalidData = 13;
    private const int ErrorNoMoreItems = 259;
    private const int ErrorNotFound = 1168;
    private static readonly IntPtr InvalidHandleValue = new(-1);

    private delegate uint CmNotifyCallback(IntPtr notifyHandle, IntPtr context, CmNotifyAction action, IntPtr eventData, uint eventDataSize);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Register_Notification(in CmNotifyFilter filter, IntPtr context, CmNotifyCallback callback, out IntPtr notifyContext);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Unregister_Notification(IntPtr notifyContext);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, DiGetClassFlags flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, StringBuilder deviceInstanceId, int deviceInstanceIdSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, SetupDiRegistryProperty property, out uint propertyRegDataType, byte[] propertyBuffer, uint propertyBufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [StructLayout(LayoutKind.Sequential)]
    private struct CmNotifyFilter
    {
        public uint cbSize;
        public uint Flags;
        public CmNotifyFilterType FilterType;
        public uint Reserved;
        public Guid ClassGuid;
    }

    private enum CmNotifyFilterType : uint
    {
        DeviceInterface = 0
    }

    private enum CmNotifyAction : uint
    {
        DeviceInterfaceArrival = 0,
        DeviceInterfaceRemoval = 1
    }

    [Flags]
    private enum DiGetClassFlags : uint
    {
        Present = 0x00000002,
        DeviceInterface = 0x00000010
    }

    private enum SetupDiRegistryProperty : uint
    {
        DeviceDescription = 0x00000000,
        HardwareId = 0x00000001,
        Manufacturer = 0x0000000B,
        FriendlyName = 0x0000000C
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDeviceInterfaceData
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public nuint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public nuint Reserved;
    }
}
