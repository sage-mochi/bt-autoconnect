using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// Phase 3b: inline Bluetooth-audio connect, no external ToothTrayCli needed.
///
/// Connecting a paired A2DP/HFP device on Windows is not a Bluetooth-API call —
/// it goes through the Core Audio stack. This mirrors what the Settings
/// "Connect" button (and ToothTray) do:
///
///   1. Enumerate render endpoints (including disconnected ones).
///   2. For each endpoint, walk its device topology across the connector to the
///      "bth"/"bthhf" kernel-streaming node on the other side.
///   3. Activate <c>IKsControl</c> on that node and send a
///      <c>KSPROPERTY_ONESHOT_RECONNECT</c> request in the
///      <c>KSPROPSETID_BtAudio</c> property set.
///
/// The target is matched by MAC (found in the bth node's device-instance id),
/// with the endpoint friendly-name as a fallback. Constants verified against
/// the Windows SDK ksmedia.h / devicetopology.h and the ToothTray source.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioConnectCom
{
    public sealed record Result(bool Found, bool Requested, string? Error)
    {
        public static Result NotFound => new(false, false, null);
    }

    // --- Constants -----------------------------------------------------------

    private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");
    private static readonly Guid IID_IKsControl           = new("28F54685-06FD-11D2-B27A-00A0C9223196");
    private static readonly Guid IID_IDeviceTopology      = new("2A07407E-6497-4A18-9787-32F79BD0D98F");
    private static readonly Guid KSPROPSETID_BtAudio      = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");

    private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new()
    {
        fmtid = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
        pid   = 14,
    };

    private const uint KSPROPERTY_ONESHOT_RECONNECT = 0;
    private const uint KSPROPERTY_TYPE_GET          = 0x00000001;
    private const uint DEVICE_STATEMASK_ALL         = 0x0000000F;
    private const int  EDATAFLOW_RENDER             = 0;
    private const uint CLSCTX_ALL                   = 23; // INPROC|HANDLER|LOCAL|REMOTE
    private const uint STGM_READ                    = 0;
    private const ushort VT_LPWSTR                  = 31;

    private const uint COINIT_MULTITHREADED = 0x0;
    private const int  S_OK = 0, S_FALSE = 1;

    // The bth KS node's device id looks like: {2}.\\?\bthenum#dev_<mac>#...
    // (or bthhfenum for the Hands-Free side). ToothTray filters on this prefix.
    private const string BthPrefix = @"{2}.\\?\bth";

    // --- Public API ----------------------------------------------------------

    /// <summary>
    /// Ask Windows to (re)connect the audio device identified by <paramref name="name"/>
    /// and/or <paramref name="address"/>. Runs COM on the calling thread — call it
    /// from a background (MTA) thread, not the UI thread.
    /// </summary>
    public static Result Reconnect(string name, ulong address)
    {
        int hrInit = CoInitializeEx(IntPtr.Zero, COINIT_MULTITHREADED);
        bool doUninit = hrInit == S_OK || hrInit == S_FALSE;

        var rcws = new List<object>();
        T Track<T>(T o) { if (o != null) rcws.Add(o); return o; }

        string macHex = address != 0 ? address.ToString("x12") : "";
        bool found = false, requested = false;
        string? error = null;

        try
        {
            var enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator)
                ?? throw new InvalidOperationException("MMDeviceEnumerator CLSID not registered.");
            var enumerator = (IMMDeviceEnumerator)Track(Activator.CreateInstance(enumType)!);

            Check(enumerator.EnumAudioEndpoints(EDATAFLOW_RENDER, DEVICE_STATEMASK_ALL, out var collection), "EnumAudioEndpoints");
            Track(collection);
            Check(collection.GetCount(out uint count), "GetCount");

            for (uint i = 0; i < count; i++)
            {
                if (collection.Item(i, out var endpoint) != S_OK || endpoint == null) continue;
                Track(endpoint);

                string? friendly = ReadFriendlyName(endpoint, rcws);
                bool nameMatch = !string.IsNullOrEmpty(name) && friendly != null &&
                                 friendly.Contains(name, StringComparison.OrdinalIgnoreCase);

                var topoIid = IID_IDeviceTopology;
                if (endpoint.Activate(ref topoIid, CLSCTX_ALL, IntPtr.Zero, out object topoObj) != S_OK)
                    continue;
                var topology = (IDeviceTopology)Track(topoObj);

                if (topology.GetConnectorCount(out uint connCount) != S_OK) continue;

                for (uint c = 0; c < connCount; c++)
                {
                    if (topology.GetConnector(c, out var connector) != S_OK || connector == null) continue;
                    Track(connector);

                    if (connector.GetConnectedTo(out var other) != S_OK || other == null) continue;
                    Track(other);

                    // The connected part lives in the other device's topology.
                    if (other is not IPart otherPart) continue;
                    if (otherPart.GetTopologyObject(out var otherTopology) != S_OK || otherTopology == null) continue;
                    Track(otherTopology);
                    if (otherTopology.GetDeviceId(out string? otherId) != S_OK || otherId == null) continue;

                    if (!otherId.StartsWith(BthPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;

                    bool macMatch = macHex.Length == 12 &&
                                    otherId.Contains(macHex, StringComparison.OrdinalIgnoreCase);
                    if (!macMatch && !nameMatch)
                        continue;

                    // This bth node belongs to our target -> fire the reconnect.
                    found = true;
                    if (enumerator.GetDevice(otherId, out var bthDevice) != S_OK || bthDevice == null) continue;
                    Track(bthDevice);

                    var ksIid = IID_IKsControl;
                    if (bthDevice.Activate(ref ksIid, CLSCTX_ALL, IntPtr.Zero, out object ksObj) != S_OK) continue;
                    var ks = (IKsControl)Track(ksObj);

                    var prop = new KSPROPERTY
                    {
                        Set   = KSPROPSETID_BtAudio,
                        Id    = KSPROPERTY_ONESHOT_RECONNECT,
                        Flags = KSPROPERTY_TYPE_GET,
                    };
                    int hr = ks.KsProperty(ref prop, (uint)Marshal.SizeOf<KSPROPERTY>(),
                                           IntPtr.Zero, 0, out _);
                    if (hr >= 0) requested = true;
                    else         error = $"KsProperty hr=0x{hr:X8}";
                }
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        finally
        {
            for (int i = rcws.Count - 1; i >= 0; i--)
                try { Marshal.FinalReleaseComObject(rcws[i]); } catch { }
            if (doUninit) CoUninitialize();
        }

        return new Result(found, requested, error);
    }

    // --- Helpers -------------------------------------------------------------

    private static void Check(int hr, string what)
    {
        if (hr < 0) throw new COMException($"{what} failed", hr);
    }

    private static string? ReadFriendlyName(IMMDevice device, List<object> rcws)
    {
        try
        {
            if (device.OpenPropertyStore(STGM_READ, out var store) != S_OK || store == null) return null;
            rcws.Add(store);
            var key = PKEY_Device_FriendlyName;
            if (store.GetValue(ref key, out var pv) != S_OK) return null;
            try { return pv.vt == VT_LPWSTR ? Marshal.PtrToStringUni(pv.pointerValue) : null; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return null; }
    }

    // --- Native ------------------------------------------------------------------

    [DllImport("ole32.dll")] private static extern int  CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern int  PropVariantClear(ref PROPVARIANT pvar);

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPERTYKEY { public Guid fmtid; public uint pid; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KSPROPERTY { public Guid Set; public uint Id; public uint Flags; }

    // PROPVARIANT: the value union starts at offset 8 on both x86 and x64
    // (VARTYPE + 3 reserved WORDs = 8-byte header). We only read VT_LPWSTR.
    [StructLayout(LayoutKind.Explicit)]
    private struct PROPVARIANT
    {
        [FieldOffset(0)] public ushort vt;
        [FieldOffset(8)] public IntPtr pointerValue;
    }

    // --- COM interfaces (vtable order matches the SDK headers exactly) ------------

    // NOTE: every method is [PreserveSig] so the declared int return IS the raw
    // HRESULT. Without it, the interop layer would treat the return as a retval
    // out-param and throw on failure — corrupting these hand-declared vtables.

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams,
                                   [MarshalAs(UnmanagedType.Interface)] out object iface);
        [PreserveSig] int OpenPropertyStore(uint stgmAccess, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDeviceTopology
    {
        [PreserveSig] int GetConnectorCount(out uint count);
        [PreserveSig] int GetConnector(uint index, out IConnector connector);
        [PreserveSig] int GetSubunitCount(out uint count);
        [PreserveSig] int GetSubunit(uint index, out IntPtr subunit);
        [PreserveSig] int GetPartById(uint id, out IntPtr part);
        [PreserveSig] int GetDeviceId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        // remaining methods (GetSignalPath, GetControlInterface...) unused
    }

    [ComImport, Guid("9C2C4058-23F5-41DE-877A-DF3AF236A09E"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IConnector
    {
        [PreserveSig] int GetType(out int type);
        [PreserveSig] int GetDataFlow(out int flow);
        [PreserveSig] int ConnectTo(IConnector other);
        [PreserveSig] int Disconnect();
        [PreserveSig] int IsConnected([MarshalAs(UnmanagedType.Bool)] out bool connected);
        [PreserveSig] int GetConnectedTo(out IConnector other);
        [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("AE2DE0E4-5BCA-4F2D-AA46-5D13F8FDB3A9"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPart
    {
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.LPWStr)] out string name);
        [PreserveSig] int GetLocalId(out uint id);
        [PreserveSig] int GetGlobalId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetPartType(out int partType);
        [PreserveSig] int GetSubType(out Guid subType);
        [PreserveSig] int GetControlInterfaceCount(out uint count);
        [PreserveSig] int GetControlInterface(uint index, out IntPtr ctrl);
        [PreserveSig] int EnumPartsIncoming(out IntPtr parts);
        [PreserveSig] int EnumPartsOutgoing(out IntPtr parts);
        [PreserveSig] int GetTopologyObject(out IDeviceTopology topology);
        // Activate / Register / Unregister unused
    }

    [ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IKsControl
    {
        [PreserveSig] int KsProperty(ref KSPROPERTY property, uint propertyLength,
                                     IntPtr propertyData, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsMethod(ref KSPROPERTY method, uint methodLength,
                                   IntPtr methodData, uint dataLength, out uint bytesReturned);
        [PreserveSig] int KsEvent(IntPtr evt, uint eventLength,
                                  IntPtr eventData, uint dataLength, out uint bytesReturned);
    }
}
