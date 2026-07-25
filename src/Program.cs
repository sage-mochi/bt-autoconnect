using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;

namespace BtAutoConnect;

[SupportedOSPlatform("windows")]
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var opts = CliOptions.Parse(args);

        // Where we live on disk -- used for tools/, config defaults, etc.
        string exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        // --- console-only diagnostic modes ---------------------------------------
        // The app is a WinExe, so these attach to the parent terminal first.
        if (opts.ShowHelp)     { ConsoleHelper.EnsureConsole(); PrintHelp();       return 0; }
        if (opts.ListDevices)  { ConsoleHelper.EnsureConsole(); return RunListDevices(); }
        if (opts.ForceRemove)  { ConsoleHelper.EnsureConsole(); return RunForceRemove(opts.Target, opts.Force); }
        if (opts.CleanupLE)    { ConsoleHelper.EnsureConsole(); return RunCleanupLE(opts.Force); }
        if (opts.TestAudio)    { ConsoleHelper.EnsureConsole(); return RunTestAudio(opts.Target); }

        // Tray is the default UI. -Console / -Background run the headless watchdog
        // (the scheduled-task / background path); -Tray forces the UI explicitly.
        bool trayMode = opts.Tray || (!opts.ConsoleMode && !opts.Background);

        // --- load config ---------------------------------------------------------
        string configPath = !string.IsNullOrWhiteSpace(opts.ConfigPath)
            ? opts.ConfigPath!
            : Path.Combine(exeDir, "config.json");

        Config cfg;
        if (File.Exists(configPath))
        {
            try { cfg = Config.Load(configPath); }
            catch (Exception ex)
            {
                if (trayMode)
                {
                    System.Windows.Forms.MessageBox.Show(
                        $"Failed to parse config:\n{configPath}\n\n{ex.Message}\n\n" +
                        "Fix or delete the file, then relaunch.",
                        "bt-autoconnect",
                        System.Windows.Forms.MessageBoxButtons.OK,
                        System.Windows.Forms.MessageBoxIcon.Error);
                    return 2;
                }
                ConsoleHelper.EnsureConsole();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"Failed to parse {configPath}: {ex.Message}");
                Console.ResetColor();
                return 2;
            }
        }
        else if (trayMode)
        {
            // First run of the tray: start with an empty watch list. The user
            // builds it from the menu (each toggle persists to this file).
            cfg = new Config();
            try { cfg.Save(configPath); } catch { /* created lazily on first toggle */ }
        }
        else
        {
            ConsoleHelper.EnsureConsole();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"No config found at:");
            Console.WriteLine($"  {configPath}");
            Console.WriteLine("Creating an example. Edit it with your earbuds' names then re-run.");
            Console.ResetColor();
            Config.WriteExample(configPath);
            return 0;
        }

        // CLI overrides config.
        if (opts.ScanIntervalSeconds is int s)    cfg.ScanIntervalSeconds    = s;
        if (opts.DropRetryWindowSeconds is int d) cfg.DropRetryWindowSeconds = d;

        // --- log setup -----------------------------------------------------------
        // Tray mode is always quiet on the console (it logs to file only).
        string logPath = ResolveLogPath(opts.LogPath, cfg.LogPath);
        var log = new Log(logPath, opts.LogMaxBytes, opts.LogKeep, trayMode || opts.Background);

        // --- mode: -Settings (standalone dialog) ---------------------------------
        if (opts.Settings)
        {
            System.Windows.Forms.Application.EnableVisualStyles();
            System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
            using var f = new SettingsForm(cfg);
            if (f.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                try { cfg.Save(configPath); } catch { /* ignore */ }
            }
            return 0;
        }

        // --- mode: tray (default) ------------------------------------------------
        if (trayMode)
        {
            new TrayApp(cfg, configPath, exeDir, log).Run();
            return 0;
        }

        // --- headless watchdog (-Console / -Background) --------------------------
        if (!opts.Background) ConsoleHelper.EnsureConsole();
        if (cfg.Devices.Count == 0)
        {
            log.Bad("No devices listed in config. Add at least one.");
            return 1;
        }

        // Audio connect is inline now (Core Audio / IKsControl), so ToothTrayCli
        // is only an optional fallback. Resolve it if present; never fail if not.
        string? ttExe = null;
        if (cfg.Devices.Any(d => d.KindNormalized == "audio"))
            ttExe = AudioConnect.Resolve(opts.ToothTrayCliPath, cfg.ToothTrayCliPath, exeDir);

        var watchdog = new Watchdog(cfg, log, ttExe);
        watchdog.LogStartupBanner();

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            watchdog.Run(cts.Token);
        }
        catch (OperationCanceledException) { /* clean shutdown */ }

        return 0;
    }

    // -------------------------------------------------------------------------
    // -ListDevices
    // -------------------------------------------------------------------------

    private static int RunListDevices()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Paired Bluetooth devices visible to BluetoothFindFirstDevice:");
        Console.ResetColor();
        Console.WriteLine();

        var all = Bluetooth.EnumerateDevices();
        if (all.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  (none found)");
            Console.WriteLine();
            Console.WriteLine("Either no devices are paired, or the Bluetooth adapter is off.");
            Console.ResetColor();
            return 0;
        }

        const string fmt = "{0,-40} {1,-20} {2,-10} {3}";

        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine(string.Format(fmt, "Name", "Address", "Connected", "State"));
        Console.WriteLine(string.Format(fmt, new string('-', 40), new string('-', 17), new string('-', 9), new string('-', 30)));
        Console.ResetColor();

        bool hasLe = false;
        foreach (var d in all)
        {
            var state = new List<string>();
            if (d.Authenticated)            state.Add("paired");
            if (d.Remembered)               state.Add("remembered");
            if (d.Name.StartsWith("LE-"))   { state.Add("LE-shadow"); hasLe = true; }

            Console.ForegroundColor = d.Connected
                ? ConsoleColor.Green
                : d.Name.StartsWith("LE-") ? ConsoleColor.DarkYellow : ConsoleColor.Gray;
            Console.WriteLine(string.Format(fmt,
                d.Name,
                Bluetooth.FormatAddress(d.Address),
                d.Connected,
                string.Join(", ", state)));
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("Tip: copy the exact Name (or Address) strings above into config.json.");
        Console.ResetColor();
        if (hasLe)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("LE-shadow entries detected. To clean them up: bt-autoconnect.exe -CleanupLE");
            Console.ResetColor();
        }
        return 0;
    }

    // -------------------------------------------------------------------------
    // -TestAudio : one-shot inline Core Audio reconnect for a single device,
    // for verifying the inline COM audio path without the tray/watchdog.
    // -------------------------------------------------------------------------

    private static int RunTestAudio(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.WriteLine("Usage: bt-autoconnect.exe -TestAudio -Target \"<device name or MAC>\"");
            return 1;
        }

        var paired   = Bluetooth.EnumerateDevices();
        ulong? want   = Bluetooth.ParseAddress(target);
        var match = want is ulong wa
            ? paired.FirstOrDefault(d => d.Address == wa)
            : paired.FirstOrDefault(d => string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase))
              ?? paired.FirstOrDefault(d => d.Name.Contains(target, StringComparison.OrdinalIgnoreCase));

        string name = match?.Name ?? target;
        ulong  addr = match?.Address ?? want ?? 0;

        Console.WriteLine($"Reconnect target: '{name}'  [{(addr == 0 ? "no MAC" : Bluetooth.FormatAddress(addr))}]");
        if (match != null) Console.WriteLine($"Currently connected: {match.Connected}");

        var r = AudioConnectCom.Reconnect(name, addr);
        Console.ForegroundColor = r.Requested ? ConsoleColor.Green
                                 : r.Found     ? ConsoleColor.Yellow
                                               : ConsoleColor.Red;
        Console.WriteLine($"Result: Found={r.Found}  Requested={r.Requested}  Error={r.Error ?? "(none)"}");
        Console.ResetColor();
        if (!r.Found)
            Console.WriteLine("No matching Bluetooth audio endpoint was located in the Core Audio graph.");
        return r.Requested ? 0 : (r.Found ? 3 : 4);
    }

    // -------------------------------------------------------------------------
    // -CleanupLE
    // -------------------------------------------------------------------------

    private static int RunCleanupLE(bool force)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Scanning for LE-shadow Bluetooth pairings...");
        Console.ResetColor();
        Console.WriteLine();

        var all = Bluetooth.EnumerateDevices();
        var le  = all.Where(d => d.Name.StartsWith("LE-", StringComparison.Ordinal)).ToList();

        if (le.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("No LE-shadow entries found. Nothing to clean up.");
            Console.ResetColor();
            return 0;
        }

        var word = le.Count == 1 ? "entry" : "entries";
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine($"Found {le.Count} LE-shadow {word}:");
        Console.ResetColor();

        foreach (var d in le)
            Console.WriteLine($"  - {d.Name,-40} {Bluetooth.FormatAddress(d.Address)}");

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.DarkGray;
        Console.WriteLine("These are typically duplicates of regular Classic pairings and a common");
        Console.WriteLine("cause of 'paired but won't connect' on Windows 10. Removing them will NOT");
        Console.WriteLine("affect the matching non-LE pairing.");
        Console.ResetColor();
        Console.WriteLine();

        if (!force)
        {
            Console.Write("Remove these entries? [y/N] ");
            var resp = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (resp != "y" && resp != "yes")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Cancelled. No devices removed.");
                Console.ResetColor();
                return 0;
            }
        }

        int ok = 0, fail = 0;
        foreach (var d in le)
        {
            var rc = Bluetooth.RemoveByAddress(d.Address);
            if (rc == 0)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"  removed: {d.Name}");
                ok++;
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"  FAILED:  {d.Name}  (err 0x{rc:X4})");
                fail++;
            }
        }
        Console.ResetColor();
        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Done. Removed {ok}, failed {fail}.");
        Console.ResetColor();
        return fail == 0 ? 0 : 3;
    }

    // -------------------------------------------------------------------------
    // -ForceRemove : escalating force-unpair of a single stubborn device, for
    // the "Remove failed" case where Settings -> Remove device does nothing.
    // -------------------------------------------------------------------------

    private static int RunForceRemove(string? target, bool force)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("Error: -ForceRemove requires -Target <device name or MAC>.");
            Console.ResetColor();
            Console.WriteLine("Run bt-autoconnect.exe -ListDevices to see names and addresses.");
            return 1;
        }

        bool isAdmin = Bluetooth.IsElevated();
        if (!isAdmin)
        {
            Console.ForegroundColor = ConsoleColor.DarkYellow;
            Console.WriteLine("Note: not running elevated. Steps 1-2 work, but the forceful steps");
            Console.WriteLine("(3-5: service restart, PnP-node removal, registry) need 'Run as administrator'.");
            Console.ResetColor();
            Console.WriteLine();
        }

        var paired   = Bluetooth.EnumerateDevices();
        ulong? wantAddr = Bluetooth.ParseAddress(target);

        List<Bluetooth.DeviceStatus> match;
        if (wantAddr is ulong wa)
        {
            match = paired.Where(d => d.Address == wa).ToList();
        }
        else
        {
            match = paired.Where(d => string.Equals(d.Name, target, StringComparison.Ordinal)).ToList();
            if (match.Count == 0)
                match = paired.Where(d => string.Equals(d.Name, target, StringComparison.OrdinalIgnoreCase)).ToList();
            if (match.Count == 0)
                match = paired.Where(d => d.Name.Contains(target, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        if (match.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"No paired device matches '{target}'.");
            Console.ResetColor();
            Console.WriteLine("Run bt-autoconnect.exe -ListDevices to see what's actually paired.");
            return 1;
        }
        if (match.Count > 1)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"'{target}' matches {match.Count} devices -- be more specific (use the MAC):");
            Console.ResetColor();
            foreach (var m in match)
                Console.WriteLine($"  - {m.Name,-40} {Bluetooth.FormatAddress(m.Address)}");
            return 1;
        }

        var dev = match[0];
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine($"Target: '{dev.Name}'  [{Bluetooth.FormatAddress(dev.Address)}]  connected={dev.Connected}");
        Console.ResetColor();
        Console.WriteLine();

        if (!force)
        {
            Console.Write("Force-remove this device? [y/N] ");
            var resp = (Console.ReadLine() ?? "").Trim().ToLowerInvariant();
            if (resp != "y" && resp != "yes")
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Cancelled.");
                Console.ResetColor();
                return 0;
            }
            Console.WriteLine();
        }

        return ForceRemoveDevice(dev, isAdmin) ? 0 : 2;
    }

    /// <summary>
    /// Escalate through progressively more forceful removal methods until the
    /// API confirms the device is gone. Returns true if it's no longer paired.
    /// </summary>
    private static bool ForceRemoveDevice(Bluetooth.DeviceStatus dev, bool isAdmin)
    {
        ulong  addr   = dev.Address;
        string macHex = addr.ToString("X12");
        string name   = string.IsNullOrEmpty(dev.Name) ? "(unnamed)" : dev.Name;

        bool StillPaired() =>
            Bluetooth.EnumerateDevices().Any(d => d.Address == addr);

        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"Force-removing '{name}'  [{Bluetooth.FormatAddress(addr)}]");
        Console.ResetColor();
        Console.WriteLine();

        // --- Step 1: drop any active service binding ------------------------
        Gray("  [1/5] Disconnecting active service bindings...");
        try { Bluetooth.AttemptConnectHid(name, addr, kick: false); } catch { }
        Thread.Sleep(400);

        // --- Step 2: the standard API (what Settings calls) -----------------
        Gray("  [2/5] BluetoothRemoveDevice API...");
        uint rc = Bluetooth.RemoveByAddress(addr);
        if (rc == 0 && !StillPaired()) { Green("        removed."); return true; }
        DarkYellow($"        did not stick (rc=0x{rc:X4}). Escalating...");

        // --- Step 3: restart the services that hold the pairing, retry ------
        if (isAdmin)
        {
            Gray("  [3/5] Restarting Bluetooth + Device Association services...");
            foreach (var svc in new[] { "bthserv", "DeviceAssociationService" })
            {
                RunProcess("sc.exe", $"stop {svc}");
                Thread.Sleep(500);
                RunProcess("sc.exe", $"start {svc}");
            }
            Thread.Sleep(2000);
            rc = Bluetooth.RemoveByAddress(addr);
            if (rc == 0 && !StillPaired()) { Green("        removed after service restart."); return true; }
            DarkYellow($"        still present (rc=0x{rc:X4}).");
        }
        else
        {
            DarkYellow("  [3/5] Skipped (needs elevation) -- restart of bthserv / DeviceAssociationService.");
        }

        // --- Step 4: remove the PnP device node(s) for this MAC -------------
        Gray("  [4/5] Removing PnP device node(s) for this MAC...");
        var nodes = Bluetooth.FindInstanceIdsByAddress(addr);
        if (nodes.Count == 0)
        {
            DarkGray("        no matching PnP nodes found.");
        }
        else if (!isAdmin)
        {
            DarkYellow($"        found {nodes.Count} node(s) but removal needs elevation:");
            foreach (var iid in nodes) DarkGray($"          {iid}");
        }
        else
        {
            foreach (var iid in nodes)
            {
                bool done = RunProcess("pnputil.exe", $"/remove-device \"{iid}\"") == 0;
                if (done) Green($"        removed {iid}");
                else      Red  ($"        FAILED  {iid}");
            }
            Thread.Sleep(1000);
            Bluetooth.RemoveByAddress(addr);
            if (!StillPaired()) { Green("        device is gone."); return true; }
        }

        // --- Step 5: delete the stale pairing key from the registry --------
        string regKey = $@"HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\{macHex}";
        if (!isAdmin)
        {
            DarkYellow("  [5/5] Skipped (needs elevation) -- registry pairing key:");
            DarkGray($"        {regKey}");
        }
        else
        {
            Gray("  [5/5] Deleting stale pairing key from registry...");
            int rrc = RunProcess("reg.exe", $"delete \"{regKey}\" /f");
            if (rrc == 0) Green("        registry key deleted.");
            else          DarkGray("        no registry key for this MAC, or it is SYSTEM-owned (a reboot may be needed).");
        }

        // --- Verdict --------------------------------------------------------
        Console.WriteLine();
        if (!StillPaired()) { Green("Device removed."); return true; }

        Red("Device still appears paired.");
        if (!isAdmin)
            Yellow("Re-run from an elevated PowerShell/console (Run as administrator) to apply steps 3-5.");
        else
            Yellow("A reboot usually clears whatever is left after the node + registry key are gone.");
        return false;
    }

    /// <summary>Run a console tool silently; returns its exit code (-1 on launch failure).</summary>
    private static int RunProcess(string file, string args)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                UseShellExecute        = false,
                CreateNoWindow         = true,
                RedirectStandardOutput = true,
                RedirectStandardError  = true,
            };
            using var p = Process.Start(psi);
            if (p == null) return -1;
            p.WaitForExit(15000);
            return p.HasExited ? p.ExitCode : -1;
        }
        catch { return -1; }
    }

    // Small colored-write helpers to keep ForceRemoveDevice readable.
    private static void Write(ConsoleColor c, string s)
    { Console.ForegroundColor = c; Console.WriteLine(s); Console.ResetColor(); }
    private static void Gray(string s)       => Write(ConsoleColor.Gray, s);
    private static void DarkGray(string s)   => Write(ConsoleColor.DarkGray, s);
    private static void DarkYellow(string s) => Write(ConsoleColor.DarkYellow, s);
    private static void Yellow(string s)     => Write(ConsoleColor.Yellow, s);
    private static void Green(string s)      => Write(ConsoleColor.Green, s);
    private static void Red(string s)        => Write(ConsoleColor.Red, s);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static string ResolveLogPath(string? cliPath, string? configPath)
    {
        if (!string.IsNullOrWhiteSpace(cliPath))    return cliPath!;
        if (!string.IsNullOrWhiteSpace(configPath)) return configPath!;

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "bt-autoconnect");
        return Path.Combine(dir, "bt-autoconnect.log");
    }


    private static void PrintHelp()
    {
        var ver = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "?";
        Console.WriteLine($"bt-autoconnect {ver}");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  bt-autoconnect.exe                          run the tray app (default)");
        Console.WriteLine("  bt-autoconnect.exe -Console                 run the watchdog in this console");
        Console.WriteLine("  bt-autoconnect.exe -Background              run the watchdog headless (logs only)");
        Console.WriteLine("  bt-autoconnect.exe -Settings                open the settings dialog and exit");
        Console.WriteLine("  bt-autoconnect.exe -ListDevices             print paired devices and exit");
        Console.WriteLine("  bt-autoconnect.exe -TestAudio -Target <name|MAC>  test inline audio reconnect");
        Console.WriteLine("  bt-autoconnect.exe -CleanupLE [-Force]      remove LE-shadow pairings");
        Console.WriteLine("  bt-autoconnect.exe -ForceRemove -Target <name|MAC> [-Force]");
        Console.WriteLine("                                              force-unpair a stuck device");
        Console.WriteLine("                                              ('Remove failed' fix; run elevated)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  -ConfigPath <path>           default: config.json next to the .exe");
        Console.WriteLine("  -LogPath <path>              default: %LOCALAPPDATA%\\bt-autoconnect\\bt-autoconnect.log");
        Console.WriteLine("  -LogMaxBytes <bytes>         default: 1048576");
        Console.WriteLine("  -LogKeep <n>                 default: 3");
        Console.WriteLine("  -ScanIntervalSeconds <n>     default: 5 (or value in config)");
        Console.WriteLine("  -DropRetryWindowSeconds <n>  default: 8 (or value in config)");
        Console.WriteLine("  -ToothTrayCliPath <path>     override discovery for toothtraycli.exe");
        Console.WriteLine("  -Background                  no console output (still logs to file)");
        Console.WriteLine("  -h, --help                   this message");
    }
}

/// <summary>
/// Tiny ad-hoc argument parser. Accepts both -Flag and --Flag styles, case
/// insensitive, with optional space- or =-separated values. Mirrors the
/// PowerShell prototype's switch names so muscle memory transfers.
/// </summary>
public sealed class CliOptions
{
    public bool    Tray                   { get; private set; }
    public bool    ConsoleMode            { get; private set; }
    public bool    Settings               { get; private set; }
    public bool    TestAudio              { get; private set; }
    public bool    ListDevices            { get; private set; }
    public bool    CleanupLE              { get; private set; }
    public bool    ForceRemove            { get; private set; }
    public string? Target                 { get; private set; }
    public bool    Force                  { get; private set; }
    public bool    Background             { get; private set; }
    public bool    ShowHelp               { get; private set; }

    public string? ConfigPath             { get; private set; }
    public string? LogPath                { get; private set; }
    public string? ToothTrayCliPath       { get; private set; }

    public int     LogMaxBytes            { get; private set; } = 1_048_576;
    public int     LogKeep                { get; private set; } = 3;
    public int?    ScanIntervalSeconds    { get; private set; }
    public int?    DropRetryWindowSeconds { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();

        for (int i = 0; i < args.Length; i++)
        {
            var raw = args[i];
            if (string.IsNullOrEmpty(raw)) continue;

            // Normalize: strip leading -/--, split on =.
            string flag, val;
            int eq = raw.IndexOf('=');
            if (eq >= 0)
            {
                flag = raw[..eq];
                val  = raw[(eq + 1)..];
            }
            else
            {
                flag = raw;
                val  = "";
            }
            flag = flag.TrimStart('-').ToLowerInvariant();

            string PeekValue()
            {
                if (!string.IsNullOrEmpty(val)) return val;
                if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                {
                    i++;
                    return args[i];
                }
                return "";
            }

            switch (flag)
            {
                case "tray":                    o.Tray        = true; break;
                case "console":                 o.ConsoleMode = true; break;
                case "settings":                o.Settings    = true; break;
                case "testaudio":               o.TestAudio   = true; break;
                case "listdevices":             o.ListDevices = true; break;
                case "cleanuple":               o.CleanupLE   = true; break;
                case "forceremove":             o.ForceRemove = true; break;
                case "target":                  o.Target      = PeekValue(); break;
                case "force":                   o.Force       = true; break;
                case "background":              o.Background  = true; break;
                case "h":
                case "help":                    o.ShowHelp    = true; break;

                case "configpath":              o.ConfigPath        = PeekValue(); break;
                case "logpath":                 o.LogPath           = PeekValue(); break;
                // All compared values are already lowercased above. Common
                // user spellings: -ToothTrayCliPath, -toothtraycli-path, etc.
                case "toothtraycli":
                case "toothtraycli-path":
                case "toothtraycli_path":
                case "toothtraycli.path":
                case "toothtraycli.exe":
                case "ttpath":
                                                o.ToothTrayCliPath = PeekValue(); break;

                case "logmaxbytes":
                    if (int.TryParse(PeekValue(), out var lmb)) o.LogMaxBytes = lmb;
                    break;
                case "logkeep":
                    if (int.TryParse(PeekValue(), out var lk))  o.LogKeep = lk;
                    break;
                case "scanintervalseconds":
                    if (int.TryParse(PeekValue(), out var sis)) o.ScanIntervalSeconds = sis;
                    break;
                case "dropretrywindowseconds":
                    if (int.TryParse(PeekValue(), out var drw)) o.DropRetryWindowSeconds = drw;
                    break;

                default:
                    // Unknown -- ignore. (Could log a warning, but keep it quiet.)
                    break;
            }
        }
        return o;
    }
}
