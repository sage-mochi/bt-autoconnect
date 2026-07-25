# bt-autoconnect — build & technical reference

Developer-facing notes for the C# tray app (build, publish, CLI, config, internals).
For the user-facing overview and downloads, see the [repo README](../README.md).

The app is distributable as a single `.exe` — with a **system-tray UI**, a **settings dialog**, and **inline audio connect** (Core Audio / `IKsControl`) so it needs no external tools.

## Tray app

Launching the exe with no arguments starts it as a tray icon (blue when a watched device is connected, grey when idle). Right- or left-click the icon for the menu:

- **Pair a new device…** — opens Windows' native "Add a device" flow.
- **Paired devices** — every paired device, each with:
  - **Auto-connect** — toggle whether the watchdog keeps this device connected. Persisted to `config.json` immediately; the background watchdog restarts so it takes effect at once.
  - **Connect now / Reconnect now** — one-off connect on demand (audio via inline Core Audio / `IKsControl`, HID via the Win32 primitive).
- **Settings…** — dialog to set the scan interval and reconnect window, and manage auto-connect / kind for every device in a grid.
- **Start with Windows** — registers/removes an HKCU `Run` entry (per-user, no admin).
- **Open log** / **Open config file** — jump to the log and to `config.json`.
- **Exit**.

The tray hosts the same watchdog on a background thread, so "open the case → it connects" works while the icon sits in the notification area. Double-clicking the icon opens Bluetooth settings.

The console modes below are still available for scripting/diagnostics.

## Build

You need the .NET 8 SDK (one-time install):

```
winget install Microsoft.DotNet.SDK.8
```

Then from inside `src/`:

```
dotnet build
```

That produces `bin\Debug\net8.0-windows10.0.19041.0\bt-autoconnect.exe`.

## Run (development)

```
dotnet run --                                  # tray app (default)
dotnet run -- -Console                         # watchdog in this console
dotnet run -- -Background                       # watchdog headless (log only)
dotnet run -- -ListDevices
dotnet run -- -CleanupLE
dotnet run -- -ForceRemove -Target "AirPods Pro"
```

Arguments after `--` are passed to the program. The exe is built as a WinExe, so the tray launches with no console window; the diagnostic modes above attach to the terminal they were started from.

### Force-removing a stubborn device

When the Settings **Remove device** button fails with *"Remove failed"*, use:

```
bt-autoconnect.exe -ForceRemove -Target "<device name or MAC>" [-Force]
```

It escalates until the pairing is gone: disconnect → `BluetoothRemoveDevice` → restart `bthserv`/`DeviceAssociationService` → remove the PnP device node(s) for that MAC via `pnputil` → delete the stale registry pairing key. Steps 1–2 work unprivileged; the forceful steps (3–5) need an **elevated** console (Run as administrator) — the tool detects elevation and tells you which steps it applied. `-Force` skips the y/N prompt.

## Publish (single-file, self-contained)

```
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:DebugType=embedded
```

Output: `bin\Release\net8.0-windows10.0.19041.0\win-x64\publish\bt-autoconnect.exe` (~180 MB, runs on any Windows 10/11 with no .NET installed). For a smaller build that relies on an installed .NET 8 Desktop Runtime, use `--self-contained false` (~25 MB).

## What's here

- `BtAutoConnect.csproj` — project file
- `Program.cs` — entry, CLI arg parsing
- `Config.cs` — JSON config model + loader + save/watch-list helpers
- `Bluetooth.cs` — Win32 BT P/Invokes (enumerate, HID connect, remove, force-remove PnP-node discovery)
- `AudioConnectCom.cs` — **inline** Core Audio / `IKsControl` reconnect (no external dependency)
- `AudioConnect.cs` — legacy ToothTrayCli shell-out, now only an optional fallback
- `Watchdog.cs` — main loop, per-device state machine
- `TrayApp.cs`, `SettingsForm.cs`, `IconFactory.cs`, `Autostart.cs`, `ConsoleHelper.cs` — tray UI + settings dialog
- `Log.cs` — rotating file logger

## Config

Place a `config.json` next to the `.exe`, or pass `-ConfigPath`. If absent, the program writes an example and exits.

Schema:

```json
{
  "devices": [
    { "name": "Bose QC Ultra Earbuds" },
    { "name": "Soundcore Space A40", "kind": "audio" },
    { "name": "AirPods Pro", "address": "AA:BB:CC:DD:EE:FF" },
    { "name": "Wireless Controller", "kind": "hid" }
  ],
  "scanIntervalSeconds": 5,
  "dropRetryWindowSeconds": 8,
  "toothTrayCliPath": ""
}
```

`kind` is `audio` (default) or `hid`. `address` is optional; if set, matching is by address first, then name. Audio devices are connected inline via the Core Audio stack — **no ToothTrayCli needed**. `toothTrayCliPath` (and a `tools/` folder or `PATH`) is honored only as a fallback if the inline path can't locate the endpoint. You normally never touch the file by hand: the tray menu and **Settings…** dialog write it for you.

## Status

Feature-complete for everyday use — inline audio + HID auto-connect, drop-recovery
watchdog, tray UI, settings dialog, and start-with-Windows all work. Remaining polish:
a signed installer / distribution. Handy diagnostics: `-TestAudio -Target "<name|MAC>"`
exercises the inline audio reconnect, and `-Settings` opens the settings dialog standalone.

## How audio connect works

Connecting a paired Bluetooth audio device isn't a Bluetooth-API call — it goes through Core Audio. `AudioConnectCom` enumerates render endpoints (including disconnected ones), walks each endpoint's device topology across its connector to the `bth`/`bthhf` kernel-streaming node, activates `IKsControl` on that node, and sends a `KSPROPERTY_ONESHOT_RECONNECT` request. The target is matched by MAC (found in the KS node's device-instance id — always known from the live paired device) with the endpoint friendly-name as a fallback. Constants were verified against the Windows SDK `ksmedia.h` / `devicetopology.h` and the ToothTray source. Every COM interface method is `[PreserveSig]` so the declared `int` return is the raw HRESULT.

Auto-connect is stored in the existing `config.json` `devices` list: a device is auto-connected iff it has an entry there, so the tray toggle / settings grid just add or remove entries (stamped with the device's address and detected `kind`).

## License

MIT — see [`../LICENSE`](../LICENSE).

## Acknowledgements

The inline audio-connect path uses the Core Audio / `IKsControl` +
`KSPROPERTY_ONESHOT_RECONNECT` technique demonstrated by
[m2jean/ToothTray](https://github.com/m2jean/ToothTray) (MIT). The relevant
constants were verified against the Windows SDK (`ksmedia.h`,
`devicetopology.h`); no ToothTray source is included here.
