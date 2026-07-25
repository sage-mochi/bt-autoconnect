using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// Win32 Bluetooth API (Bthprops.cpl) wrapper. Two connect paths:
///   * Audio devices go through Windows Core Audio (see AudioConnect.cs).
///   * HID devices (controllers, keyboards, mice) use BluetoothSetServiceState
///     with the HID service UUID 0x1124 -- the case where this API is the
///     correct primitive. For audio profiles it returns 0x57 by design.
/// </summary>
[SupportedOSPlatform("windows")]
public static class Bluetooth
{
    // --- Public managed types ------------------------------------------------

    public sealed record DeviceStatus(
        string Name,
        ulong  Address,
        bool   Connected,
        bool   Authenticated,
        bool   Remembered,
        uint   ClassOfDevice)
    {
        /// <summary>
        /// Best-guess device kind ("audio" or "hid") from the Class-of-Device
        /// major-class bits. Peripheral (0x05) covers keyboards/mice/controllers;
        /// everything else defaults to audio, which is the common case here.
        /// </summary>
        public string GuessedKind => Bluetooth.ClassifyKind(ClassOfDevice);

        /// <summary>True if this looks like a stray Windows 10 "LE-..." shadow pairing.</summary>
        public bool IsLeShadow => Name.StartsWith("LE-", StringComparison.Ordinal);
    }

    public sealed record HidConnectResult(
        bool Found,
        bool WasConnected,
        uint EnableError,    // 0 == success
        uint DisableError,   // populated only if Kicked
        bool Kicked);

    // --- HID service GUID ----------------------------------------------------

    private static readonly Guid HidServiceGuid =
        new("00001124-0000-1000-8000-00805F9B34FB");

    private const uint BLUETOOTH_SERVICE_DISABLE = 0x00;
    private const uint BLUETOOTH_SERVICE_ENABLE  = 0x01;

    // --- P/Invoke surface ----------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay;
        public ushort wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint    dwSize;
        public ulong   Address;
        public uint    ulClassofDevice;
        [MarshalAs(UnmanagedType.Bool)] public bool fConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string  szName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint    dwSize;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnAuthenticated;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnRemembered;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnUnknown;
        [MarshalAs(UnmanagedType.Bool)] public bool fReturnConnected;
        [MarshalAs(UnmanagedType.Bool)] public bool fIssueInquiry;
        public byte    cTimeoutMultiplier;
        public IntPtr  hRadio;
    }

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(
        ref BLUETOOTH_DEVICE_SEARCH_PARAMS pbtsp,
        ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("Bthprops.cpl", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindNextDevice(
        IntPtr hFind,
        ref BLUETOOTH_DEVICE_INFO pbtdi);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BluetoothFindDeviceClose(IntPtr hFind);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothSetServiceState(
        IntPtr hRadio,
        ref BLUETOOTH_DEVICE_INFO pbtdi,
        ref Guid pGuidService,
        uint dwServiceFlags);

    [DllImport("Bthprops.cpl", SetLastError = true)]
    private static extern uint BluetoothRemoveDevice(ref ulong pAddress);

    // --- cfgmgr32: enumerate PnP device instance IDs -------------------------
    // Used by force-remove to find every device node that belongs to a given
    // Bluetooth MAC (the MAC appears in the instance ID), so we can remove the
    // stuck nodes with pnputil when BluetoothRemoveDevice alone won't do it.

    private const int  CR_SUCCESS               = 0;
    private const uint CM_GETIDLIST_FILTER_NONE = 0x00000000;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(
        out uint pulLen, string? pszFilter, uint ulFlags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(
        string? pszFilter, char[] Buffer, uint BufferLen, uint ulFlags);

    // --- Internal helpers ----------------------------------------------------

    private static BLUETOOTH_DEVICE_SEARCH_PARAMS NewSearch() =>
        new()
        {
            dwSize               = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
            fReturnAuthenticated = true,
            fReturnRemembered    = true,
            fReturnUnknown       = false,
            fReturnConnected     = true,
            fIssueInquiry        = false,
            cTimeoutMultiplier   = 0,
            hRadio               = IntPtr.Zero,
        };

    private static BLUETOOTH_DEVICE_INFO NewInfo() =>
        new()
        {
            dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>(),
            szName = string.Empty,
        };

    // --- Public API ----------------------------------------------------------

    /// <summary>
    /// Enumerate paired Bluetooth devices. Cached lookup, fast (~ms).
    /// </summary>
    public static List<DeviceStatus> EnumerateDevices()
    {
        var sp   = NewSearch();
        var info = NewInfo();
        var list = new List<DeviceStatus>();

        IntPtr h = BluetoothFindFirstDevice(ref sp, ref info);
        if (h == IntPtr.Zero) return list;

        try
        {
            do
            {
                list.Add(new DeviceStatus(
                    Name:          info.szName,
                    Address:       info.Address,
                    Connected:     info.fConnected,
                    Authenticated: info.fAuthenticated,
                    Remembered:    info.fRemembered,
                    ClassOfDevice: info.ulClassofDevice));
                info = NewInfo();
            } while (BluetoothFindNextDevice(h, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(h);
        }
        return list;
    }

    /// <summary>
    /// Unpair a Bluetooth device by address. Returns the Win32 error code
    /// (0 on success).
    /// </summary>
    public static uint RemoveByAddress(ulong address) =>
        BluetoothRemoveDevice(ref address);

    /// <summary>
    /// True if the current process is running elevated (Administrator). The
    /// service-restart, PnP-node-removal, and registry steps of force-remove
    /// need this; the plain BluetoothRemoveDevice call does not.
    /// </summary>
    public static bool IsElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            var pr = new System.Security.Principal.WindowsPrincipal(id);
            return pr.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>
    /// Return every PnP device-instance ID whose identifier contains the given
    /// Bluetooth MAC. One paired pair of earbuds typically spawns several nodes
    /// (BTHENUM enumerator, A2DP/HFP audio endpoints, AVRCP, LE GATT, ...), all
    /// of which carry the 12-hex-digit MAC in their instance ID. Read-only and
    /// safe to call non-elevated.
    /// </summary>
    public static List<string> FindInstanceIdsByAddress(ulong address)
    {
        var result = new List<string>();
        string macHex = address.ToString("X12");

        if (CM_Get_Device_ID_List_SizeW(out uint len, null, CM_GETIDLIST_FILTER_NONE) != CR_SUCCESS
            || len == 0)
            return result;

        var buf = new char[len];
        if (CM_Get_Device_ID_ListW(null, buf, len, CM_GETIDLIST_FILTER_NONE) != CR_SUCCESS)
            return result;

        // Buffer is a double-null-terminated list of null-separated strings.
        foreach (var id in new string(buf).Split('\0',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var hexOnly = new string(id.Where(Uri.IsHexDigit).ToArray());
            if (hexOnly.Contains(macHex, StringComparison.OrdinalIgnoreCase))
                result.Add(id);
        }
        return result;
    }

    /// <summary>
    /// Trigger a HID-class device (controller, keyboard, mouse) to connect
    /// by toggling the HID service binding. If <paramref name="kick"/> is
    /// true, the function first disables and then re-enables the service --
    /// the move that forces Windows to rebind a stuck HID driver. Otherwise
    /// it just enables (idempotent, safe to call on already-bound devices).
    /// </summary>
    public static HidConnectResult AttemptConnectHid(string targetName, ulong? targetAddress, bool kick)
    {
        var sp   = NewSearch();
        var info = NewInfo();

        IntPtr h = BluetoothFindFirstDevice(ref sp, ref info);
        if (h == IntPtr.Zero)
            return new HidConnectResult(false, false, 0, 0, false);

        try
        {
            do
            {
                bool match =
                    (targetAddress is ulong wantAddr && info.Address == wantAddr) ||
                    string.Equals(info.szName, targetName, StringComparison.Ordinal);

                if (!match)
                {
                    info = NewInfo();
                    continue;
                }

                uint disableErr = 0;
                var  g = HidServiceGuid;

                if (kick)
                {
                    disableErr = BluetoothSetServiceState(
                        IntPtr.Zero, ref info, ref g, BLUETOOTH_SERVICE_DISABLE);
                    Thread.Sleep(250);
                }

                uint enableErr = BluetoothSetServiceState(
                    IntPtr.Zero, ref info, ref g, BLUETOOTH_SERVICE_ENABLE);

                return new HidConnectResult(
                    Found:        true,
                    WasConnected: info.fConnected,
                    EnableError:  enableErr,
                    DisableError: disableErr,
                    Kicked:       kick);

            } while (BluetoothFindNextDevice(h, ref info));
        }
        finally
        {
            BluetoothFindDeviceClose(h);
        }

        return new HidConnectResult(false, false, 0, 0, false);
    }

    // --- Class-of-Device classification -------------------------------------

    /// <summary>
    /// Map a Bluetooth Class-of-Device value to the config "kind" this program
    /// uses. Only the major device class (bits 8..12) matters here: 0x05 is the
    /// "Peripheral" class (keyboard / mouse / joystick / gamepad) which we route
    /// through the HID connect primitive; everything else -- most importantly
    /// 0x04 "Audio/Video" -- goes through the audio (ToothTrayCli) path, which is
    /// also the safe default for unknown classes.
    /// </summary>
    public static string ClassifyKind(uint classOfDevice)
    {
        uint major = (classOfDevice >> 8) & 0x1F;
        return major == 0x05 ? "hid" : "audio";
    }

    // --- Address formatting helpers -----------------------------------------

    public static string FormatAddress(ulong addr)
    {
        var hex = addr.ToString("X12");
        return string.Join(':',
            hex[0..2], hex[2..4], hex[4..6], hex[6..8], hex[8..10], hex[10..12]);
    }

    /// <summary>
    /// Parse a Bluetooth MAC string in any reasonable format
    /// (colon, dash, dot, or no separator; any casing). Returns null on
    /// malformed input.
    /// </summary>
    public static ulong? ParseAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        Span<char> buf  = stackalloc char[12];
        int        used = 0;
        foreach (var c in text!)
        {
            if (used == 12) return null;     // too many hex digits
            if (IsHex(c)) buf[used++] = c;
        }
        if (used != 12) return null;

        return ulong.TryParse(buf, System.Globalization.NumberStyles.HexNumber,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out var v) ? v : null;
    }

    private static bool IsHex(char c) =>
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
}
