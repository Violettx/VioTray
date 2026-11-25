"""VXE / ATK 鼠标电量 HID 探测脚本

直接调用 hid.dll / setupapi.dll，无第三方依赖。
协议来源：python-pulsar-mouse-tool + Linux hid-pulsar.c
  report id 0x08, 17 字节, cmd 0x04 = POWER
  checksum = (0x55 - sum(buf[0:16])) & 0xFF
  resp[6] = 电量%, resp[7] = 充电中, resp[8:10] = 毫伏(大端)
"""

import ctypes
from ctypes import wintypes as wt

hid = ctypes.WinDLL("hid.dll", use_last_error=True)
setupapi = ctypes.WinDLL("setupapi.dll", use_last_error=True)
kernel32 = ctypes.WinDLL("kernel32.dll", use_last_error=True)

DIGCF_PRESENT = 0x02
DIGCF_DEVICEINTERFACE = 0x10
GENERIC_READ = 0x80000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_RW = 0x03
OPEN_EXISTING = 3
FILE_FLAG_OVERLAPPED = 0x40000000
ERROR_IO_PENDING = 997
WAIT_OBJECT_0 = 0
INVALID_HANDLE = ctypes.c_void_p(-1).value


class GUID(ctypes.Structure):
    _fields_ = [("Data1", wt.DWORD), ("Data2", wt.WORD), ("Data3", wt.WORD),
                ("Data4", ctypes.c_ubyte * 8)]


class SP_DEVICE_INTERFACE_DATA(ctypes.Structure):
    _fields_ = [("cbSize", wt.DWORD), ("InterfaceClassGuid", GUID),
                ("Flags", wt.DWORD), ("Reserved", ctypes.c_void_p)]


class SP_DEVICE_INTERFACE_DETAIL(ctypes.Structure):
    _pack_ = 1
    _fields_ = [("cbSize", wt.DWORD), ("DevicePath", wt.WCHAR * 520)]


class HIDD_ATTRIBUTES(ctypes.Structure):
    _fields_ = [("Size", wt.ULONG), ("VendorID", wt.USHORT),
                ("ProductID", wt.USHORT), ("VersionNumber", wt.USHORT)]


class HIDP_CAPS(ctypes.Structure):
    _fields_ = [
        ("Usage", wt.USHORT), ("UsagePage", wt.USHORT),
        ("InputReportByteLength", wt.USHORT), ("OutputReportByteLength", wt.USHORT),
        ("FeatureReportByteLength", wt.USHORT),
        ("Reserved", wt.USHORT * 17),
        ("NumberLinkCollectionNodes", wt.USHORT),
        ("NumberInputButtonCaps", wt.USHORT), ("NumberInputValueCaps", wt.USHORT),
        ("NumberInputDataIndices", wt.USHORT),
        ("NumberOutputButtonCaps", wt.USHORT), ("NumberOutputValueCaps", wt.USHORT),
        ("NumberOutputDataIndices", wt.USHORT),
        ("NumberFeatureButtonCaps", wt.USHORT), ("NumberFeatureValueCaps", wt.USHORT),
        ("NumberFeatureDataIndices", wt.USHORT),
    ]


class OVERLAPPED(ctypes.Structure):
    _fields_ = [("Internal", ctypes.c_size_t), ("InternalHigh", ctypes.c_size_t),
                ("Offset", wt.DWORD), ("OffsetHigh", wt.DWORD), ("hEvent", wt.HANDLE)]


hid.HidD_GetHidGuid.argtypes = [ctypes.POINTER(GUID)]
hid.HidD_GetAttributes.argtypes = [wt.HANDLE, ctypes.POINTER(HIDD_ATTRIBUTES)]
hid.HidD_GetPreparsedData.argtypes = [wt.HANDLE, ctypes.POINTER(ctypes.c_void_p)]
hid.HidD_SetOutputReport.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.ULONG]
hid.HidD_SetFeature.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.ULONG]
hid.HidD_GetFeature.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.ULONG]
hid.HidD_FreePreparsedData.argtypes = [ctypes.c_void_p]
hid.HidP_GetCaps.argtypes = [ctypes.c_void_p, ctypes.POINTER(HIDP_CAPS)]
hid.HidP_GetCaps.restype = ctypes.c_long

setupapi.SetupDiGetClassDevsW.argtypes = [ctypes.POINTER(GUID), wt.LPCWSTR,
                                         wt.HWND, wt.DWORD]
setupapi.SetupDiGetClassDevsW.restype = wt.HANDLE
setupapi.SetupDiEnumDeviceInterfaces.argtypes = [wt.HANDLE, ctypes.c_void_p,
                                                 ctypes.POINTER(GUID), wt.DWORD,
                                                 ctypes.POINTER(SP_DEVICE_INTERFACE_DATA)]
setupapi.SetupDiGetDeviceInterfaceDetailW.argtypes = [
    wt.HANDLE, ctypes.POINTER(SP_DEVICE_INTERFACE_DATA),
    ctypes.POINTER(SP_DEVICE_INTERFACE_DETAIL), wt.DWORD,
    ctypes.POINTER(wt.DWORD), ctypes.c_void_p]
setupapi.SetupDiDestroyDeviceInfoList.argtypes = [wt.HANDLE]

kernel32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.c_void_p,
                                 wt.DWORD, wt.DWORD, wt.HANDLE]
kernel32.CreateFileW.restype = wt.HANDLE
kernel32.ReadFile.argtypes = [wt.HANDLE, ctypes.c_void_p, wt.DWORD,
                              ctypes.POINTER(wt.DWORD), ctypes.POINTER(OVERLAPPED)]
kernel32.GetOverlappedResult.argtypes = [wt.HANDLE, ctypes.POINTER(OVERLAPPED),
                                         ctypes.POINTER(wt.DWORD), wt.BOOL]
kernel32.CreateEventW.restype = wt.HANDLE


def list_hid_paths():
    guid = GUID()
    hid.HidD_GetHidGuid(ctypes.byref(guid))
    hdev = setupapi.SetupDiGetClassDevsW(ctypes.byref(guid), None, None,
                                         DIGCF_PRESENT | DIGCF_DEVICEINTERFACE)
    if hdev == INVALID_HANDLE or hdev is None:
        raise OSError("SetupDiGetClassDevs failed")
    out = []
    idx = 0
    while True:
        did = SP_DEVICE_INTERFACE_DATA()
        did.cbSize = ctypes.sizeof(SP_DEVICE_INTERFACE_DATA)
        if not setupapi.SetupDiEnumDeviceInterfaces(hdev, None, ctypes.byref(guid),
                                                    idx, ctypes.byref(did)):
            break
        idx += 1
        req = wt.DWORD(0)
        setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(did), None, 0,
                                                  ctypes.byref(req), None)
        if req.value == 0:
            continue
        detail = SP_DEVICE_INTERFACE_DETAIL()
        detail.cbSize = 8 if ctypes.sizeof(ctypes.c_void_p) == 8 else 6
        if setupapi.SetupDiGetDeviceInterfaceDetailW(hdev, ctypes.byref(did),
                                                     ctypes.byref(detail), req.value,
                                                     ctypes.byref(req), None):
            out.append(detail.DevicePath)
    setupapi.SetupDiDestroyDeviceInfoList(hdev)
    return out


def open_device(path):
    h = kernel32.CreateFileW(path, GENERIC_READ | GENERIC_WRITE, FILE_SHARE_RW,
                             None, OPEN_EXISTING, FILE_FLAG_OVERLAPPED, None)
    if h == INVALID_HANDLE:
        h = kernel32.CreateFileW(path, 0, FILE_SHARE_RW, None, OPEN_EXISTING,
                                 FILE_FLAG_OVERLAPPED, None)
    return h


def get_caps(h):
    pp = ctypes.c_void_p()
    if not hid.HidD_GetPreparsedData(h, ctypes.byref(pp)):
        return None
    caps = HIDP_CAPS()
    hid.HidP_GetCaps(pp, ctypes.byref(caps))
    hid.HidD_FreePreparsedData(pp)
    return caps


def read_report(h, size, timeout_ms=400):
    ov = OVERLAPPED()
    ov.hEvent = kernel32.CreateEventW(None, True, False, None)
    buf = ctypes.create_string_buffer(size)
    got = wt.DWORD(0)
    ok = kernel32.ReadFile(h, buf, size, ctypes.byref(got), ctypes.byref(ov))
    if not ok:
        if ctypes.get_last_error() != ERROR_IO_PENDING:
            kernel32.CloseHandle(ov.hEvent)
            return None
        if kernel32.WaitForSingleObject(ov.hEvent, timeout_ms) != WAIT_OBJECT_0:
            kernel32.CancelIo(h)
            kernel32.CloseHandle(ov.hEvent)
            return None
        if not kernel32.GetOverlappedResult(h, ctypes.byref(ov), ctypes.byref(got), False):
            kernel32.CloseHandle(ov.hEvent)
            return None
    data = buf.raw[:got.value]
    kernel32.CloseHandle(ov.hEvent)
    return data


def build_payload(cmd):
    body = [0x08, cmd] + [0] * 14
    cs = (0x55 - sum(body)) & 0xFF
    return bytes(body + [cs])


def query_power(h):
    payload = build_payload(0x04)
    buf = ctypes.create_string_buffer(payload, len(payload))
    sent = "none"
    if hid.HidD_SetOutputReport(h, buf, len(payload)):
        sent = "SetOutputReport"
    else:
        buf2 = ctypes.create_string_buffer(payload, len(payload))
        if hid.HidD_SetFeature(h, buf2, len(payload)):
            sent = "SetFeature"
        else:
            ok = kernel32.WriteFile(h, buf, len(payload), ctypes.byref(wt.DWORD(0)), None)
            if ok:
                sent = "WriteFile"
    resp = read_report(h, 64)
    return sent, resp


def main():
    paths = list_hid_paths()
    print(f"total HID collections: {len(paths)}\n")

    targets = []
    for p in paths:
        h = open_device(p)
        if h == INVALID_HANDLE:
            continue
        attrs = HIDD_ATTRIBUTES()
        attrs.Size = ctypes.sizeof(HIDD_ATTRIBUTES)
        if not hid.HidD_GetAttributes(h, ctypes.byref(attrs)):
            kernel32.CloseHandle(h)
            continue
        caps = get_caps(h)
        if attrs.VendorID in (0x3554, 0x258A):
            print(f"VID={attrs.VendorID:04X} PID={attrs.ProductID:04X} "
                  f"usagePage=0x{caps.UsagePage:02X} usage=0x{caps.Usage:02X} "
                  f"in={caps.InputReportByteLength} out={caps.OutputReportByteLength} "
                  f"feat={caps.FeatureReportByteLength}")
            print(f"   {p}")
            targets.append((attrs.VendorID, attrs.ProductID, p, h, caps))
        else:
            kernel32.CloseHandle(h)

    print("\n--- probing POWER command on each candidate ---")
    for vid, pid, p, h, caps in targets:
        sent, resp = query_power(h)
        tag = f"VID={vid:04X} PID={pid:04X} up=0x{caps.UsagePage:02X} u=0x{caps.Usage:02X}"
        if resp:
            hexs = " ".join(f"{b:02X}" for b in resp[:20])
            print(f"[{tag}] sent={sent} resp[{len(resp)}] = {hexs}")
            if len(resp) >= 10 and resp[0] == 0x08 and resp[1] == 0x04:
                pct, chg = resp[6], resp[7]
                mv = (resp[8] << 8) | resp[9]
                print(f"    >>> BATTERY {pct}%  charging={bool(chg)}  {mv} mV")
        else:
            print(f"[{tag}] sent={sent} resp=None (timeout)")

    for *_, h, caps in targets:
        kernel32.CloseHandle(h)


if __name__ == "__main__":
    main()
