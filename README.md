# bt-autoconnect

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

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). From [`phase3/`](phase3/):

```
dotnet build                 # debug build
dotnet run                   # run the tray app
```

Single-file, self-contained publish:

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=embedded
```

See [`phase3/README.md`](phase3/README.md) for the full build/publish/usage reference.

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

## How it works

Two API surfaces, because Bluetooth audio connect on Windows isn't a Bluetooth-API call:

- **Status** (paired / connected / in range) comes from the Win32 `BluetoothFindFirstDevice` API, in-process.
- **Audio connect** goes through the **Core Audio** stack: enumerate render endpoints, walk the device topology to the `bth` kernel-streaming node, and send a `KSPROPERTY_ONESHOT_RECONNECT` request via `IKsControl` — the same thing the Settings "Connect" button does internally. This is inline (no external tools).
- **HID connect** (controllers etc.) uses `BluetoothSetServiceState` with the HID service UUID, which *is* the right primitive for that class.

A per-device watchdog polls on an interval, connects anything enabled-but-disconnected, and retries devices that drop right after connecting.

The app also includes fixes for common Windows 10 "paired but won't connect" states: `-CleanupLE` removes stray `LE-…` shadow pairings, and `-ForceRemove` escalates through service restarts, PnP-node removal, and registry cleanup for devices the Settings "Remove" button can't shift.

Deeper technical notes (COM interop specifics, verified constants, CLI reference) live in [`phase3/README.md`](phase3/README.md).

---

## License

MIT — see [`LICENSE`](LICENSE). The inline audio-connect technique is acknowledged from the MIT-licensed [m2jean/ToothTray](https://github.com/m2jean/ToothTray); no ToothTray source is included here.
