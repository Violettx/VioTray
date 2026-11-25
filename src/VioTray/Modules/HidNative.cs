using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace VioTray.Modules;

internal sealed record HidCollectionInfo(
    string Path,
    ushort VendorId,
    ushort ProductId,
    ushort UsagePage,
    ushort Usage,
    int InputReportLength,
    int OutputReportLength);

/// <summary>
/// 直接调 hid.dll / setupapi.dll。不用第三方库，逻辑和 tools/probe_hid.py 一致。
/// </summary>
internal static class HidNative
{
    private const uint DIGCF_PRESENT = 0x02;
    private const uint DIGCF_DEVICEINTERFACE = 0x10;

    private const uint GENERIC_READ = 0x80000000;
    private const uint GENERIC_WRITE = 0x40000000;
    private const uint FILE_SHARE_READ = 0x01;
    private const uint FILE_SHARE_WRITE = 0x02;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OVERLAPPED = 0x40000000;

    private const int ERROR_IO_PENDING = 997;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVICE_INTERFACE_DATA
    {
        public uint cbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDD_ATTRIBUTES
    {
        public uint Size;
        public ushort VendorID;
        public ushort ProductID;
        public ushort VersionNumber;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;

        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid hidGuid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(SafeFileHandle handle, ref HIDD_ATTRIBUTES attributes);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(SafeFileHandle handle, out IntPtr preparsedData);

    [DllImport("hid.dll")]
    private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS caps);

    [DllImport("hid.dll")]
    private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_SetOutputReport(SafeFileHandle handle, byte[] buffer, uint bufferLength);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator,
        IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
        ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
        ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData,
        uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode,
        IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(SafeFileHandle file, byte[] buffer, uint numberOfBytesToRead,
        out uint numberOfBytesRead, ref NativeOverlapped overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetOverlappedResult(SafeFileHandle file, ref NativeOverlapped overlapped,
        out uint numberOfBytesTransferred, bool wait);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CancelIoEx(SafeFileHandle file, ref NativeOverlapped overlapped);

    public static List<HidCollectionInfo> Enumerate(ushort vendorId)
    {
        var found = new List<HidCollectionInfo>();

        HidD_GetHidGuid(out var hidGuid);
        var set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
            DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);

        if (set == IntPtr.Zero || set == new IntPtr(-1))
            return found;

        try
        {
            var data = new SP_DEVICE_INTERFACE_DATA
            {
                cbSize = (uint)Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>(),
            };

            for (uint index = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, index, ref data); index++)
            {
                var path = GetDevicePath(set, ref data);
                if (path is null)
                    continue;

                using var device = HidDevice.Open(path);
                if (device is null || device.VendorId != vendorId)
                    continue;

                found.Add(new HidCollectionInfo(
                    path,
                    device.VendorId,
                    device.ProductId,
                    device.UsagePage,
                    device.Usage,
                    device.InputReportLength,
                    device.OutputReportLength));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }

        return found;
    }

    private static string? GetDevicePath(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data)
    {
        SetupDiGetDeviceInterfaceDetail(set, ref data, IntPtr.Zero, 0, out var required, IntPtr.Zero);
        if (required == 0)
            return null;

        var buffer = Marshal.AllocHGlobal((int)required);
        try
        {
            Marshal.WriteInt32(buffer, 0, IntPtr.Size == 8 ? 8 : 6);
            if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, required, out _, IntPtr.Zero))
                return null;

            return Marshal.PtrToStringUni(buffer + 4);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal sealed class HidDevice : IDisposable
    {
        private readonly SafeFileHandle _handle;
        private bool _disposed;

        public ushort VendorId { get; private set; }
        public ushort ProductId { get; private set; }
        public ushort UsagePage { get; private set; }
        public ushort Usage { get; private set; }
        public int InputReportLength { get; private set; }
        public int OutputReportLength { get; private set; }

        private HidDevice(SafeFileHandle handle)
        {
            _handle = handle;
        }

        public static HidDevice? Open(string path)
        {
            var handle = CreateFile(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED, IntPtr.Zero);

            if (handle.IsInvalid)
                return null;

            var device = new HidDevice(handle);
            if (!device.LoadInfo())
            {
                device.Dispose();
                return null;
            }

            return device;
        }

        private bool LoadInfo()
        {
            var attributes = new HIDD_ATTRIBUTES { Size = (uint)Marshal.SizeOf<HIDD_ATTRIBUTES>() };
            if (!HidD_GetAttributes(_handle, ref attributes))
                return false;

            VendorId = attributes.VendorID;
            ProductId = attributes.ProductID;

            if (!HidD_GetPreparsedData(_handle, out var preparsed))
                return false;

            try
            {
                var caps = new HIDP_CAPS();
                if (HidP_GetCaps(preparsed, ref caps) < 0)
                    return false;

                Usage = caps.Usage;
                UsagePage = caps.UsagePage;
                InputReportLength = caps.InputReportByteLength;
                OutputReportLength = caps.OutputReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }

            return true;
        }

        public bool SetOutputReport(byte[] payload)
        {
            if (_disposed)
                return false;

            return HidD_SetOutputReport(_handle, payload, (uint)payload.Length);
        }

        public byte[]? ReadReport(int size, int timeoutMs)
        {
            if (_disposed || size <= 0)
                return null;

            var buffer = new byte[size];
            using var done = new ManualResetEvent(false);
            var overlapped = new NativeOverlapped
            {
                EventHandle = done.SafeWaitHandle.DangerousGetHandle(),
            };

            try
            {
                if (ReadFile(_handle, buffer, (uint)size, out var read, ref overlapped))
                    return buffer[..(int)read];

                if (Marshal.GetLastWin32Error() != ERROR_IO_PENDING)
                    return null;

                if (!done.WaitOne(timeoutMs))
                {
                    CancelIoEx(_handle, ref overlapped);
                    return null;
                }

                if (!GetOverlappedResult(_handle, ref overlapped, out read, false))
                    return null;

                return buffer[..(int)read];
            }
            catch
            {
                return null;
            }
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _handle.Dispose();
        }
    }
}
