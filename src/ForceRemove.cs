using System.Diagnostics;
using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// Escalating force-unpair of a single stubborn device, for the "Remove failed"
/// case where Settings → Remove device (and the plain BluetoothRemoveDevice API
/// behind it) won't shift the pairing. Shared by the CLI (`-ForceRemove`) and the
/// tray menu; progress is reported through a callback so each caller can render
/// it however it likes (console colors / log / balloons).
///
/// Steps 1–2 work unprivileged; steps 3–5 need elevation.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ForceRemove
{
    public enum Level { Step, Info, Ok, Warn, Bad }

    /// <summary>
    /// Escalate through progressively more forceful removal methods until the API
    /// confirms the device is gone. Returns true if it's no longer paired.
    /// </summary>
    public static bool Run(Bluetooth.DeviceStatus dev, bool isAdmin, Action<Level, string> report)
    {
        ulong  addr   = dev.Address;
        string macHex = addr.ToString("X12");
        string name   = string.IsNullOrEmpty(dev.Name) ? "(unnamed)" : dev.Name;

        bool StillPaired() =>
            Bluetooth.EnumerateDevices().Any(d => d.Address == addr);

        report(Level.Info, $"Force-removing '{name}'  [{Bluetooth.FormatAddress(addr)}]");

        // --- Step 1: drop any active service binding ------------------------
        report(Level.Step, "  [1/5] Disconnecting active service bindings...");
        try { Bluetooth.AttemptConnectHid(name, addr, kick: false); } catch { }
        Thread.Sleep(400);

        // --- Step 2: the standard API (what Settings calls) -----------------
        report(Level.Step, "  [2/5] BluetoothRemoveDevice API...");
        uint rc = Bluetooth.RemoveByAddress(addr);
        if (rc == 0 && !StillPaired()) { report(Level.Ok, "        removed."); return true; }
        report(Level.Warn, $"        did not stick (rc=0x{rc:X4}). Escalating...");

        // --- Step 3: restart the services that hold the pairing, retry ------
        if (isAdmin)
        {
            report(Level.Step, "  [3/5] Restarting Bluetooth + Device Association services...");
            foreach (var svc in new[] { "bthserv", "DeviceAssociationService" })
            {
                RunProcess("sc.exe", $"stop {svc}");
                Thread.Sleep(500);
                RunProcess("sc.exe", $"start {svc}");
            }
            Thread.Sleep(2000);
            rc = Bluetooth.RemoveByAddress(addr);
            if (rc == 0 && !StillPaired()) { report(Level.Ok, "        removed after service restart."); return true; }
            report(Level.Warn, $"        still present (rc=0x{rc:X4}).");
        }
        else
        {
            report(Level.Warn, "  [3/5] Skipped (needs elevation) -- restart of bthserv / DeviceAssociationService.");
        }

        // --- Step 4: remove the PnP device node(s) for this MAC -------------
        report(Level.Step, "  [4/5] Removing PnP device node(s) for this MAC...");
        var nodes = Bluetooth.FindInstanceIdsByAddress(addr);
        if (nodes.Count == 0)
        {
            report(Level.Info, "        no matching PnP nodes found.");
        }
        else if (!isAdmin)
        {
            report(Level.Warn, $"        found {nodes.Count} node(s) but removal needs elevation:");
            foreach (var iid in nodes) report(Level.Info, $"          {iid}");
        }
        else
        {
            foreach (var iid in nodes)
            {
                bool done = RunProcess("pnputil.exe", $"/remove-device \"{iid}\"") == 0;
                report(done ? Level.Ok : Level.Bad, (done ? "        removed  " : "        FAILED   ") + iid);
            }
            Thread.Sleep(1000);
            Bluetooth.RemoveByAddress(addr);
            if (!StillPaired()) { report(Level.Ok, "        device is gone."); return true; }
        }

        // --- Step 5: delete the stale pairing key from the registry --------
        string regKey = $@"HKLM\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices\{macHex}";
        if (!isAdmin)
        {
            report(Level.Warn, "  [5/5] Skipped (needs elevation) -- registry pairing key:");
            report(Level.Info, $"        {regKey}");
        }
        else
        {
            report(Level.Step, "  [5/5] Deleting stale pairing key from registry...");
            int rrc = RunProcess("reg.exe", $"delete \"{regKey}\" /f");
            if (rrc == 0) report(Level.Ok, "        registry key deleted.");
            else          report(Level.Info, "        no registry key for this MAC, or it is SYSTEM-owned (a reboot may be needed).");
        }

        // --- Verdict --------------------------------------------------------
        if (!StillPaired()) { report(Level.Ok, "Device removed."); return true; }

        report(Level.Bad, "Device still appears paired.");
        report(Level.Warn, isAdmin
            ? "A reboot usually clears whatever is left after the node + registry key are gone."
            : "Re-run elevated (Run as administrator) to apply steps 3-5.");
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
}
