using System.Diagnostics;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace BtAutoConnect;

/// <summary>
/// The tray application. Wraps the existing <see cref="Watchdog"/> in a
/// system-tray UI:
///   * pair a new device (hands off to the Windows "Add device" flow),
///   * toggle auto-connect per paired device (persisted to config.json),
///   * connect a device on demand,
///   * start with Windows, open the log / config, quit.
///
/// The watchdog runs on a background thread and is restarted whenever the watch
/// list changes, so toggling auto-connect takes effect immediately.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayApp
{
    private readonly string _exeDir;
    private readonly string _configPath;
    private readonly Log    _log;

    private Config _config;

    private NotifyIcon      _tray   = null!;
    private ContextMenuStrip _menu  = null!;
    private System.Windows.Forms.Timer _refresh = null!;
    private Icon? _currentIcon;
    private bool  _lastConnected;
    private SynchronizationContext? _ui;   // WinForms UI thread, for marshaling

    // Watchdog host state.
    private CancellationTokenSource? _cts;
    private Task?  _watchdogTask;

    public TrayApp(Config config, string configPath, string exeDir, Log log)
    {
        _config     = config;
        _configPath = configPath;
        _exeDir     = exeDir;
        _log        = log;
    }

    // -------------------------------------------------------------------------
    // Entry point
    // -------------------------------------------------------------------------

    public void Run()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try { Application.SetHighDpiMode(HighDpiMode.SystemAware); } catch { }

        _menu = new ContextMenuStrip();
        _menu.Opening += (_, _) => RebuildMenu();

        // Creating a WinForms control above installs the WindowsFormsSynchronization
        // context; capture it so background tasks can post UI work back here.
        _ui = SynchronizationContext.Current;

        _currentIcon = IconFactory.Create(connected: false);
        _tray = new NotifyIcon
        {
            Icon             = _currentIcon,
            Text             = "bt-autoconnect",
            Visible          = true,
            ContextMenuStrip = _menu,
        };
        // Left-click should also open the menu (more discoverable than right-only).
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) _menu.Show(Control.MousePosition);
        };
        _tray.DoubleClick += (_, _) => OpenBluetoothSettings();

        StartWatchdog();
        UpdateStatus();

        _refresh = new System.Windows.Forms.Timer { Interval = 3000 };
        _refresh.Tick += (_, _) => UpdateStatus();
        _refresh.Start();

        Application.ApplicationExit += (_, _) => Cleanup();
        Application.Run();
    }

    // -------------------------------------------------------------------------
    // Watchdog host
    // -------------------------------------------------------------------------

    private string? ResolveToothTray() =>
        _config.Devices.Any(d => d.KindNormalized == "audio")
            ? AudioConnect.Resolve(null, _config.ToothTrayCliPath, _exeDir)
            : null;

    private void StartWatchdog()
    {
        StopWatchdog();

        // Audio connect is inline (Core Audio / IKsControl); ToothTrayCli is only
        // resolved as an optional fallback, so its absence is not a problem.
        string? ttExe = ResolveToothTray();

        var cfg = _config;
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        _watchdogTask = Task.Run(() =>
        {
            try
            {
                var wd = new Watchdog(cfg, _log, ttExe);
                wd.LogStartupBanner();
                wd.Run(token);
            }
            catch (OperationCanceledException) { /* restart / shutdown */ }
            catch (Exception ex) { _log.Bad($"Watchdog crashed: {ex.Message}"); }
        }, token);
    }

    private void StopWatchdog()
    {
        try { _cts?.Cancel(); } catch { }
        try { _watchdogTask?.Wait(2500); } catch { }
        try { _cts?.Dispose(); } catch { }
        _cts = null;
        _watchdogTask = null;
    }

    /// <summary>Persist the config and bounce the watchdog so changes take hold.</summary>
    private void SaveAndRestart()
    {
        try { _config.Save(_configPath); }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "Could not save config",
                ex.Message, ToolTipIcon.Error);
            return;
        }
        StartWatchdog();
        UpdateStatus();
    }

    // -------------------------------------------------------------------------
    // Status (icon + tooltip)
    // -------------------------------------------------------------------------

    private List<Bluetooth.DeviceStatus> SafeEnumerate()
    {
        try { return Bluetooth.EnumerateDevices(); }
        catch { return new List<Bluetooth.DeviceStatus>(); }
    }

    private void UpdateStatus()
    {
        var paired  = SafeEnumerate();
        var watched = paired.Where(_config.IsWatched).ToList();
        var live    = watched.Where(d => d.Connected).Select(d => d.Name).ToList();

        string tip;
        if (live.Count > 0)      tip = "Connected: " + string.Join(", ", live);
        else if (watched.Count > 0) tip = $"Watching {watched.Count} device" +
                                          (watched.Count == 1 ? "" : "s");
        else                     tip = "No devices set to auto-connect";

        // NotifyIcon.Text is capped at 63 chars.
        _tray.Text = tip.Length <= 63 ? tip : tip[..60] + "...";

        bool connected = live.Count > 0;
        if (connected != _lastConnected || _currentIcon == null)
        {
            _lastConnected = connected;
            var newIcon = IconFactory.Create(connected);
            _tray.Icon = newIcon;
            IconFactory.Destroy(_currentIcon);
            _currentIcon = newIcon;
        }
    }

    // -------------------------------------------------------------------------
    // Menu
    // -------------------------------------------------------------------------

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        var paired = SafeEnumerate();

        var watched = paired.Where(_config.IsWatched).ToList();
        var live    = watched.Where(d => d.Connected).ToList();

        string header = live.Count > 0
            ? "● Connected: " + string.Join(", ", live.Select(d => d.Name))
            : watched.Count > 0
                ? $"Watching {watched.Count} device(s)"
                : "No devices set to auto-connect";
        var head = new ToolStripMenuItem(Trim(header, 60)) { Enabled = false };
        _menu.Items.Add(head);
        _menu.Items.Add(new ToolStripSeparator());

        var pair = new ToolStripMenuItem("Pair a new device…");
        pair.Click += (_, _) => OpenBluetoothSettings();
        _menu.Items.Add(pair);
        _menu.Items.Add(new ToolStripSeparator());

        // Real (non-LE-shadow) paired devices, connected first then by name.
        var devices = paired
            .Where(d => !d.IsLeShadow && !string.IsNullOrWhiteSpace(d.Name))
            .OrderByDescending(d => d.Connected)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        _menu.Items.Add(new ToolStripMenuItem("Paired devices") { Enabled = false });

        if (devices.Count == 0)
        {
            _menu.Items.Add(new ToolStripMenuItem("  (none — pair one first)") { Enabled = false });
        }
        else
        {
            foreach (var dev in devices)
                _menu.Items.Add(BuildDeviceItem(dev));
        }

        _menu.Items.Add(new ToolStripSeparator());

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        _menu.Items.Add(settings);

        var startup = new ToolStripMenuItem("Start with Windows")
        {
            Checked      = Autostart.IsEnabled(),
            CheckOnClick = false,
            Enabled      = Autostart.Available,
        };
        startup.Click += (_, _) => ToggleAutostart(startup);
        _menu.Items.Add(startup);

        var openLog = new ToolStripMenuItem("Open log");
        openLog.Click += (_, _) => OpenPath(_log.Path, selectInFolder: false);
        _menu.Items.Add(openLog);

        var openCfg = new ToolStripMenuItem("Open config file");
        openCfg.Click += (_, _) => OpenPath(_configPath, selectInFolder: true);
        _menu.Items.Add(openCfg);

        _menu.Items.Add(new ToolStripSeparator());

        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        _menu.Items.Add(exit);
    }

    private ToolStripMenuItem BuildDeviceItem(Bluetooth.DeviceStatus dev)
    {
        bool watched   = _config.IsWatched(dev);
        string label   = dev.Name + (dev.Connected ? "   ●" : "");
        var item = new ToolStripMenuItem(label);
        if (dev.Connected) item.ForeColor = System.Drawing.Color.Green;

        var auto = new ToolStripMenuItem("Auto-connect")
        {
            Checked      = watched,
            CheckOnClick = false,
        };
        auto.Click += (_, _) => ToggleWatch(dev);
        item.DropDownItems.Add(auto);

        var connectNow = new ToolStripMenuItem(dev.Connected ? "Reconnect now" : "Connect now");
        connectNow.Click += (_, _) => ConnectNow(dev);
        item.DropDownItems.Add(connectNow);

        var kindHint = new ToolStripMenuItem(
            $"Kind: {dev.GuessedKind}   [{Bluetooth.FormatAddress(dev.Address)}]")
        { Enabled = false };
        item.DropDownItems.Add(kindHint);

        return item;
    }

    // -------------------------------------------------------------------------
    // Actions
    // -------------------------------------------------------------------------

    private void ToggleWatch(Bluetooth.DeviceStatus dev)
    {
        if (_config.IsWatched(dev)) _config.RemoveWatch(dev);
        else                        _config.AddWatch(dev);
        SaveAndRestart();
    }

    private void OpenSettings()
    {
        using var form = new SettingsForm(_config);
        // Modal; TopMost pulls it in front when invoked from the tray (which has
        // no owner window to activate it).
        form.TopMost = true;
        form.Shown += (_, _) => { form.TopMost = false; form.Activate(); };
        if (form.ShowDialog() == DialogResult.OK)
            SaveAndRestart();
    }

    private void ToggleAutostart(ToolStripMenuItem item)
    {
        if (Autostart.IsEnabled()) Autostart.Disable();
        else                       Autostart.Enable();
        item.Checked = Autostart.IsEnabled();
    }

    /// <summary>
    /// Fire a one-off connect for a device regardless of whether it's in the
    /// watch list. Runs off the UI thread (the audio path can take seconds).
    /// </summary>
    private void ConnectNow(Bluetooth.DeviceStatus dev)
    {
        string name = dev.Name;
        string kind = dev.GuessedKind;

        Task.Run(() =>
        {
            string title, body;
            var icon = ToolTipIcon.Info;
            try
            {
                if (kind == "hid")
                {
                    var res = Bluetooth.AttemptConnectHid(name, dev.Address, kick: dev.Connected);
                    if (res.Found && res.EnableError == 0)
                        { title = "Connecting"; body = $"{name}: requested (waiting for link)."; }
                    else
                        { title = "Connect failed"; body = $"{name}: HID enable error 0x{res.EnableError:X4}."; icon = ToolTipIcon.Warning; }
                }
                else
                {
                    // Inline Core Audio path first.
                    var com = AudioConnectCom.Reconnect(name, dev.Address);
                    if (com.Found && com.Requested)
                        { title = "Connecting"; body = $"{name}: requested (waiting for link)."; }
                    else
                    {
                        // Fall back to ToothTrayCli if the endpoint wasn't found.
                        var ttExe = AudioConnect.Resolve(null, _config.ToothTrayCliPath, _exeDir);
                        if (!com.Found && ttExe != null && AudioConnect.Connect(ttExe, name).ExitCode == 0)
                            { title = "Connecting"; body = $"{name}: requested (waiting for link)."; }
                        else
                        {
                            title = "Connect failed";
                            body  = com.Error != null
                                ? $"{name}: {com.Error}"
                                : $"{name}: no audio endpoint found (out of range?).";
                            icon  = ToolTipIcon.Warning;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                title = "Connect failed"; body = $"{name}: {ex.Message}"; icon = ToolTipIcon.Error;
            }

            void Show() { try { _tray.ShowBalloonTip(4000, title, Trim(body, 220), icon); } catch { } }
            if (_ui != null) _ui.Post(_ => Show(), null);
            else             Show();
        });
    }

    private void OpenBluetoothSettings()
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
        }
        catch
        {
            // Fall back to the classic control-panel devices page.
            try { Process.Start(new ProcessStartInfo("control", "/name Microsoft.DevicesAndPrinters") { UseShellExecute = true }); }
            catch (Exception ex)
            {
                _tray.ShowBalloonTip(5000, "Couldn't open Bluetooth settings", ex.Message, ToolTipIcon.Error);
            }
        }
    }

    private void OpenPath(string path, bool selectInFolder)
    {
        try
        {
            if (selectInFolder && File.Exists(path))
                Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                    Process.Start("explorer.exe", $"\"{dir}\"");
                }
            }
        }
        catch (Exception ex)
        {
            _tray.ShowBalloonTip(5000, "Couldn't open", $"{path}\n{ex.Message}", ToolTipIcon.Error);
        }
    }

    private void ExitApp()
    {
        _refresh?.Stop();
        StopWatchdog();
        if (_tray != null) _tray.Visible = false;
        Application.Exit();
    }

    private void Cleanup()
    {
        StopWatchdog();
        try { if (_tray != null) { _tray.Visible = false; _tray.Dispose(); } } catch { }
        IconFactory.Destroy(_currentIcon);
        _currentIcon = null;
    }

    private static string Trim(string s, int max) =>
        s.Length <= max ? s : s[..(max - 1)] + "…";
}
