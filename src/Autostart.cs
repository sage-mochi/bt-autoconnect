using Microsoft.Win32;
using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// "Start with Windows" via the per-user Run key (HKCU). No admin needed, and
/// it launches at logon for this user only. The tray app starts minimized to
/// the notify area, so there's nothing else to configure.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "bt-autoconnect";

    /// <summary>Full path to the running .exe (not the dotnet host).</summary>
    private static string? ExePath =>
        Environment.ProcessPath is string p &&
        p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p : null;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
            return key?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    /// <summary>Whether autostart can be offered at all (needs a real .exe path).</summary>
    public static bool Available => ExePath != null;

    /// <summary>The exe path autostart would register (or currently points at).</summary>
    public static string? TargetPath => ExePath;

    /// <summary>The path currently registered in the Run key, if any.</summary>
    public static string? RegisteredPath
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: false);
                return (key?.GetValue(ValueName) as string)?.Trim('"');
            }
            catch { return null; }
        }
    }

    /// <summary>
    /// True when we're running out of a build-output folder. Registering that path
    /// for autostart is fragile -- a rebuild or clean deletes it, silently breaking
    /// startup -- so the UI warns and suggests installing a copy somewhere stable.
    /// </summary>
    public static bool IsRunningFromBuildOutput =>
        ExePath is string p &&
        (p.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
         p.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase));

    public static void Enable()
    {
        if (ExePath is not string exe) return;
        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        key?.SetValue(ValueName, $"\"{exe}\"");
    }

    public static void Disable()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        catch { /* ignore */ }
    }
}
