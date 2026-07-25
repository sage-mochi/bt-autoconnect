<p align="center">
  <img src="assets/icon.png" width="120" alt="bt-autoconnect icon">
</p>

<h1 align="center">bt-autoconnect</h1>

**Auto-connect your Bluetooth earbuds (and controllers) on Windows — the "open the case and it just works" behavior macOS has, that Windows doesn't.**

Windows can *pair* a Bluetooth audio device, but it won't automatically reconnect when the device comes back in range, and it won't fight back when a flaky device connects then immediately drops. `bt-autoconnect` is a tiny system-tray app that fixes both:

- **Proximity reconnect** — when a device you've enabled comes into range (you open the case, power on the headphones), it connects them for you.
- **Drop-recovery watchdog** — if a device connects and then drops within a few seconds (broken drivers / power management / profile issues), it retries until it sticks.
- **Per-device, your choice** — toggle auto-connect for each paired device from the tray. It's not hardcoded to any brand — Bose, Apple, Soundcore, Sony, Samsung, JBL, controllers, whatever you pair.

Works with any Bluetooth **audio** device (A2DP/HFP) and any Bluetooth **HID** device (game controllers, keyboards, mice).

---

## Install

### Download (no build required)

Grab the latest exe from the [**Releases**](https://github.com/sage-mochi/bt-autoconnect/releases) page:

| File | When to use | .NET required? |
|------|-------------|----------------|
| `bt-autoconnect-vX.Y.Z-win-x64.exe` | Just want it to run | **No** — fully self-contained |
| `bt-autoconnect-vX.Y.Z-win-x64-netdependent.exe` | You already have .NET 8 | Yes — [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) |

Run the exe → it appears in your system tray. That's it. There's nothing to install and nothing runs as administrator.

> **SmartScreen note:** the exe isn't code-signed, so Windows SmartScreen may show a "Windows protected your PC" prompt the first time. Click **More info → Run anyway**. (You can verify what it does — the full source is right here.)

### Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (`winget install Microsoft.DotNet.SDK.8`). From [`src/`](src/):

```
dotnet build                 # debug build
dotnet run                   # run the tray app
```

Single-file publish:

```
# Self-contained (~180 MB, needs no .NET on the target machine)
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded

# Framework-dependent (~25 MB, needs the .NET 8 Desktop Runtime installed)
dotnet publish -c Release -r win-x64 --self-contained false ^
  -p:PublishSingleFile=true -p:DebugType=embedded
```

The output lands in `src/bin/Release/net8.0-windows10.0.19041.0/win-x64/publish/`.

---

## Using it

Left- or right-click the tray icon (blue when a watched device is connected, grey when idle):

- **Pair a new device…** — opens Windows' native "Add a device" flow.
- **Paired devices** — every paired device, each with **Auto-connect** (toggle whether the watchdog keeps it connected) and **Connect now**.
- **Settings…** — scan interval, reconnect window, and an auto-connect / kind grid for every device.
- **Start with Windows** — launches at login (per-user, no admin).
- **Open log / config**, and **Exit**.

Turn on **Auto-connect** for your earbuds, enable **Start with Windows**, and you're done: log in, open the case, they connect.

---

## Configuration

The tray menu and **Settings…** dialog write `config.json` for you, so you normally never edit it by hand. If you want to, it lives next to the exe (or pass `-ConfigPath`):

```json
{
  "devices": [
    { "name": "Bose QC Ultra Earbuds" },
    { "name": "Soundcore Space A40", "kind": "audio" },
    { "name": "AirPods Pro", "address": "AA:BB:CC:DD:EE:FF" },
    { "name": "Wireless Controller", "kind": "hid" }
  ],
  "scanIntervalSeconds": 5,
  "dropRetryWindowSeconds": 8
}
```

A device is auto-connected exactly when it has an entry in `devices`. `kind` is `audio` (default) or `hid`. `address` is optional; when set, matching is by address first, then name (so it survives renames).

---

## Command line

The tray is the default, but the same exe runs headless and offers diagnostics. Run these from a terminal — output attaches to the console you launched from:

| Command | What it does |
|---------|--------------|
| `bt-autoconnect.exe` | run the tray app (default) |
| `-Console` / `-Background` | run the watchdog in the console / fully headless |
| `-Settings` | open the settings dialog standalone |
| `-ListDevices` | print paired devices + connection state |
| `-TestAudio -Target "<name\|MAC>"` | test the inline audio reconnect for one device |
| `-CleanupLE [-Force]` | remove stray `LE-…` shadow pairings |
| `-ForceRemove -Target "<name\|MAC>" [-Force]` | force-unpair a stuck device |

**Force-removing a stuck device.** When Windows' **Remove device** fails with *"Remove failed"*, `-ForceRemove` escalates until the pairing is gone: disconnect → `BluetoothRemoveDevice` → restart `bthserv`/`DeviceAssociationService` → remove the PnP node(s) for that MAC via `pnputil` → delete the stale registry pairing key. The first two steps work unprivileged; the forceful ones need an elevated console (the tool detects elevation and reports what it applied).

---

## How it works

Two API surfaces, because Bluetooth audio connect on Windows isn't a Bluetooth-API call:

- **Status** (paired / connected / in range) comes from the Win32 `BluetoothFindFirstDevice` API, in-process.
- **Audio connect** goes through the **Core Audio** stack: enumerate render endpoints, walk the device topology to the `bth` kernel-streaming node, and send a `KSPROPERTY_ONESHOT_RECONNECT` request via `IKsControl` — the same thing the Settings "Connect" button does internally. This is inline (no external tools).
- **HID connect** (controllers etc.) uses `BluetoothSetServiceState` with the HID service UUID, which *is* the right primitive for that class.

A per-device watchdog polls on an interval, connects anything enabled-but-disconnected, and retries devices that drop right after connecting.

The audio path matches the target by MAC (found in the KS node's device-instance id) with a friendly-name fallback, and every hand-declared COM interface method is `[PreserveSig]` so the raw HRESULT is respected. The `KSPROPSETID_BtAudio` / `KSPROPERTY_ONESHOT_RECONNECT` constants were verified against the Windows SDK (`ksmedia.h`, `devicetopology.h`) and the [ToothTray](https://github.com/m2jean/ToothTray) source. The source in [`src/`](src/) is organized one concern per file (`AudioConnectCom.cs`, `Watchdog.cs`, `TrayApp.cs`, `Bluetooth.cs`, …).

---

## License

MIT — see [`LICENSE`](LICENSE). The inline audio-connect technique is acknowledged from the MIT-licensed [m2jean/ToothTray](https://github.com/m2jean/ToothTray); no ToothTray source is included here.
