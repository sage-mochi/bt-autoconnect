using System.Diagnostics;
using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// Audio connect via ToothTrayCli. Legacy fallback only: the inline Core Audio
/// path (see AudioConnectCom) is primary, and this is used just when the inline
/// path can't locate the endpoint and a ToothTrayCli binary happens to be present.
/// </summary>
[SupportedOSPlatform("windows")]
public static class AudioConnect
{
    public sealed record InvokeResult(int ExitCode, string Stdout, string Stderr);

    /// <summary>
    /// Find toothtraycli.exe / ToothTray.exe. Returns null if not found.
    /// Search order: explicit path, config path, ./tools/, alongside exe, PATH.
    /// </summary>
    public static string? Resolve(string? explicitPath, string? configPath, string exeDir)
    {
        var names = new[]
        {
            "toothtraycli.exe", "ToothTrayCli.exe",
            "toothtray.exe",    "ToothTray.exe",
        };

        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(explicitPath)) candidates.Add(explicitPath!);
        if (!string.IsNullOrWhiteSpace(configPath))   candidates.Add(configPath!);

        foreach (var dir in new[] { Path.Combine(exeDir, "tools"), exeDir })
        {
            foreach (var name in names)
            {
                candidates.Add(Path.Combine(dir, name));
            }
        }

        foreach (var path in candidates)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                try { return Path.GetFullPath(path); } catch { return path; }
            }
        }

        // PATH fallback.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                try
                {
                    var p = Path.Combine(dir.Trim(), name);
                    if (File.Exists(p)) return p;
                }
                catch { /* ignore malformed PATH entries */ }
            }
        }
        return null;
    }

    /// <summary>
    /// Run "<exe> connect <deviceName>" and capture exit code + streams.
    /// </summary>
    public static InvokeResult Connect(string exePath, string deviceName, int timeoutMs = 10_000)
    {
        var psi = new ProcessStartInfo
        {
            FileName               = exePath,
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        psi.ArgumentList.Add("connect");
        psi.ArgumentList.Add(deviceName);

        using var proc = new Process { StartInfo = psi };
        proc.Start();

        // Read both pipes async so a large stderr can't deadlock waiting on
        // exit. ReadToEndAsync starts pumping immediately.
        var outTask = proc.StandardOutput.ReadToEndAsync();
        var errTask = proc.StandardError.ReadToEndAsync();

        if (!proc.WaitForExit(timeoutMs))
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            return new InvokeResult(-1, "", $"(timed out after {timeoutMs} ms)");
        }

        // After WaitForExit, the async reads should finish quickly.
        return new InvokeResult(
            ExitCode: proc.ExitCode,
            Stdout:   outTask.GetAwaiter().GetResult() ?? "",
            Stderr:   errTask.GetAwaiter().GetResult() ?? "");
    }
}
