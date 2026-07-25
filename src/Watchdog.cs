using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// Main watchdog loop. Same per-device state machine as the PowerShell
/// prototype, ported to C#. Polls paired devices on an interval; for each
/// watched device that isn't currently connected, triggers a connect via
/// the appropriate primitive (audio -> ToothTrayCli, hid -> Win32).
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class Watchdog
{
    private readonly Config  _config;
    private readonly Log     _log;
    private readonly string? _ttExe;          // null if no audio devices
    private readonly Dictionary<string, DeviceState> _state = new();

    private enum Phase { Idle, Connecting, Connected, Recovering }

    private sealed class DeviceState
    {
        public Phase   Phase           = Phase.Idle;
        public DateTime? LastConnected = null;
        public DateTime? LastAttempt   = null;
        public string?  LastErrorLogged = null;
    }

    public Watchdog(Config config, Log log, string? toothTrayCliExe)
    {
        _config = config;
        _log    = log;
        _ttExe  = toothTrayCliExe;
        foreach (var d in _config.Devices) _state[d.Name] = new DeviceState();
    }

    // --- Static helper: pick the paired device that matches a config entry --

    public static Bluetooth.DeviceStatus? ResolveMatch(
        DeviceConfig cfg, IEnumerable<Bluetooth.DeviceStatus> paired)
    {
        if (cfg.AddressParsed is ulong wantAddr)
        {
            var hit = paired.FirstOrDefault(p => p.Address == wantAddr);
            if (hit != null) return hit;
        }
        if (!string.IsNullOrEmpty(cfg.Name))
        {
            return paired.FirstOrDefault(
                p => string.Equals(p.Name, cfg.Name, StringComparison.Ordinal));
        }
        return null;
    }

    // --- Startup sanity check ------------------------------------------------

    public void LogStartupBanner()
    {
        var names = string.Join(", ", _config.Devices.Select(d => d.Name));
        _log.Note($"bt-autoconnect started. Watching: {names}");
        _log.Quiet(_ttExe != null
            ? $"Audio connect: inline (Core Audio / IKsControl), ToothTrayCli fallback: {_ttExe}"
            : "Audio connect: inline (Core Audio / IKsControl)");
        _log.Quiet($"Scan interval: {_config.ScanIntervalSeconds}s   |   " +
                   $"Drop window: {_config.DropRetryWindowSeconds}s");

        try
        {
            var paired = Bluetooth.EnumerateDevices();
            var names2 = paired.Select(p => p.Name).ToList();

            foreach (var d in _config.Devices)
            {
                if (ResolveMatch(d, paired) != null) continue;

                _log.Warn($"WARNING: '{d.Name}' is in config but not in the paired-device list.");

                if (!string.IsNullOrWhiteSpace(d.Address))
                {
                    if (d.AddressParsed is ulong addr)
                        _log.Hint($"  No paired device has address {Bluetooth.FormatAddress(addr)}.");
                    else
                        _log.Hint($"  Address '{d.Address}' is not a valid MAC (need 12 hex digits).");
                }

                var hints = names2.Where(n =>
                    string.Equals(n, d.Name, StringComparison.OrdinalIgnoreCase) ||
                    n.Contains(d.Name, StringComparison.OrdinalIgnoreCase) ||
                    d.Name.Contains(n, StringComparison.OrdinalIgnoreCase)).ToList();
                if (hints.Count > 0)
                    _log.Hint("  Did you mean: " + string.Join(" | ", hints));
            }

            _log.Quiet($"Currently paired ({names2.Count}): {string.Join(" | ", names2)}");
            _log.Quiet("(Run with -ListDevices to see exact names + connection state.)");
        }
        catch (Exception ex)
        {
            _log.Bad($"Startup enumeration failed: {ex.Message}");
        }
    }

    // --- Main loop -----------------------------------------------------------

    public void Run(CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            List<Bluetooth.DeviceStatus> paired;
            try
            {
                paired = Bluetooth.EnumerateDevices();
            }
            catch (Exception ex)
            {
                _log.Bad($"Failed to enumerate paired devices: {ex.Message}");
                WaitInterval(cancel);
                continue;
            }

            foreach (var cfg in _config.Devices)
            {
                var st    = _state[cfg.Name];
                var match = ResolveMatch(cfg, paired);
                if (match == null) continue;

                var now = DateTime.Now;

                // Already connected?
                if (match.Connected)
                {
                    if (st.Phase != Phase.Connected)
                        _log.Ok($"{cfg.Name}: connected.");
                    st.Phase           = Phase.Connected;
                    st.LastConnected   = now;
                    st.LastErrorLogged = null;
                    continue;
                }

                // Was connected last cycle -> this is a drop. Enter recovery.
                if (st.Phase == Phase.Connected)
                {
                    var ago = (now - (st.LastConnected ?? now)).TotalSeconds;
                    if (ago < _config.DropRetryWindowSeconds)
                        _log.Warn($"{cfg.Name}: dropped after {ago:F1}s -- retrying.");
                    else
                        _log.Quiet($"{cfg.Name}: disconnected.");
                    st.Phase = Phase.Recovering;
                }

                st.LastAttempt = now;

                // Route by kind.
                if (cfg.KindNormalized == "hid")
                    AttemptHid(cfg, st);
                else
                    AttemptAudio(cfg, st, match);
            }

            WaitInterval(cancel);
        }
    }

    private void WaitInterval(CancellationToken cancel)
    {
        var ms = Math.Max(_config.ScanIntervalSeconds, 1) * 1000;
        try { Task.Delay(ms, cancel).Wait(cancel); } catch { /* cancelled */ }
    }

    // --- Audio path ----------------------------------------------------------

    /// <summary>
    /// Try the inline Core Audio (IKsControl) reconnect first. Only if
    /// the audio endpoint can't be located do we fall back to ToothTrayCli (when
    /// present). The MAC comes from the live paired device, so matching is exact.
    /// </summary>
    private void AttemptAudio(DeviceConfig cfg, DeviceState st, Bluetooth.DeviceStatus match)
    {
        AudioConnectCom.Result com;
        try
        {
            com = AudioConnectCom.Reconnect(match.Name, match.Address);
        }
        catch (Exception ex)
        {
            var key = "audio:com:exc:" + ex.Message;
            if (st.LastErrorLogged != key)
            {
                _log.Bad($"{cfg.Name}: inline audio connect threw: {ex.Message}");
                st.LastErrorLogged = key;
            }
            com = AudioConnectCom.Result.NotFound;
        }

        if (com.Found && com.Requested)
        {
            st.LastErrorLogged = null;
            st.Phase = Phase.Connecting;
            _log.Note($"{cfg.Name}: connect requested ok (waiting for link).");
            return;
        }

        if (com.Found && !com.Requested)
        {
            var key = "audio:com:fail:" + (com.Error ?? "?");
            if (st.LastErrorLogged != key || st.Phase == Phase.Recovering)
            {
                var msg = $"{cfg.Name}: audio reconnect request failed ({com.Error}).";
                if (st.Phase == Phase.Recovering) _log.Bad(msg); else _log.Quiet(msg);
                st.LastErrorLogged = key;
            }
            return;
        }

        // Endpoint not found via the audio stack. Fall back to ToothTrayCli if
        // the user supplied it; otherwise stay quiet until an actual drop.
        if (_ttExe != null) { AttemptAudioViaToothTray(cfg, st); return; }

        var key2 = "audio:notfound";
        if (st.LastErrorLogged == key2 && st.Phase != Phase.Recovering) return;
        var m = com.Error != null
            ? $"{cfg.Name}: audio connect error: {com.Error}"
            : $"{cfg.Name}: no audio endpoint yet (out of range?). Will keep trying quietly.";
        if (st.Phase == Phase.Recovering) _log.Bad(m); else _log.Quiet(m);
        st.LastErrorLogged = key2;
    }

    // --- Audio path (legacy ToothTrayCli fallback) --------------------------

    private void AttemptAudioViaToothTray(DeviceConfig cfg, DeviceState st)
    {
        if (_ttExe == null) return;

        AudioConnect.InvokeResult result;
        try
        {
            result = AudioConnect.Connect(_ttExe, cfg.Name);
        }
        catch (Exception ex)
        {
            var errKey = "audio:exc:" + ex.GetType().Name + ":" + ex.Message;
            if (st.LastErrorLogged != errKey)
            {
                _log.Bad($"{cfg.Name}: connect threw: {ex.Message}");
                st.LastErrorLogged = errKey;
            }
            return;
        }

        if (result.ExitCode == 0)
        {
            st.LastErrorLogged = null;
            st.Phase = Phase.Connecting;
            _log.Note($"{cfg.Name}: connect requested ok (waiting for link).");
            return;
        }

        var errKey2 = $"audio:exit={result.ExitCode};err={result.Stderr.Trim()}";
        if (st.LastErrorLogged == errKey2 && st.Phase != Phase.Recovering) return;

        var msg = $"{cfg.Name}: connect failed (exit {result.ExitCode})";
        var stderr = result.Stderr.Trim();
        var stdout = result.Stdout.Trim();
        if (!string.IsNullOrEmpty(stderr))      msg += $" | stderr: {stderr}";
        else if (!string.IsNullOrEmpty(stdout)) msg += $" | stdout: {stdout}";

        if (st.Phase == Phase.Recovering) _log.Bad(msg);
        else                              _log.Quiet(msg);
        st.LastErrorLogged = errKey2;
    }

    // --- HID path ------------------------------------------------------------

    private void AttemptHid(DeviceConfig cfg, DeviceState st)
    {
        var kick = st.Phase == Phase.Recovering;

        Bluetooth.HidConnectResult res;
        try
        {
            res = Bluetooth.AttemptConnectHid(cfg.Name, cfg.AddressParsed, kick);
        }
        catch (Exception ex)
        {
            var errKey = "hid:exc:" + ex.GetType().Name + ":" + ex.Message;
            if (st.LastErrorLogged != errKey)
            {
                _log.Bad($"{cfg.Name}: HID connect threw: {ex.Message}");
                st.LastErrorLogged = errKey;
            }
            return;
        }

        if (!res.Found) return;   // disappeared mid-cycle; harmless

        if (res.EnableError == 0)
        {
            if (st.LastErrorLogged != null) st.LastErrorLogged = null;
            if (st.Phase != Phase.Connecting)
            {
                var tag = res.Kicked ? "kick" : "enable";
                _log.Note($"{cfg.Name}: HID {tag} ok (waiting for link).");
            }
            st.Phase = Phase.Connecting;
            return;
        }

        var errKey2 = $"hid:en=0x{res.EnableError:X4};dis=0x{res.DisableError:X4};kick={(res.Kicked ? 1 : 0)}";
        if (st.LastErrorLogged == errKey2 && st.Phase != Phase.Recovering) return;

        var detail = $"enable=0x{res.EnableError:X4}";
        if (res.Kicked) detail = $"disable=0x{res.DisableError:X4} " + detail;

        var msg = st.Phase == Phase.Recovering
            ? $"{cfg.Name}: HID connect failed -- {detail}"
            : $"{cfg.Name}: not in range ({detail}). Will keep trying quietly.";

        if (st.Phase == Phase.Recovering) _log.Bad(msg);
        else                              _log.Quiet(msg);
        st.LastErrorLogged = errKey2;
    }
}
