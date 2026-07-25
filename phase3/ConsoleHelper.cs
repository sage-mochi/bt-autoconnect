using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// The app is built as a WinExe (subsystem: Windows) so launching the tray from
/// Explorer never flashes a console window. But the diagnostic CLI modes
/// (-ListDevices, -CleanupLE, -ForceRemove, -Console, --help) still want to
/// print to the terminal they were started from. AttachConsole(ATTACH_PARENT_PROCESS)
/// hooks our stdout/stderr up to the parent cmd/PowerShell when there is one;
/// if there isn't (double-clicked), we fall back to allocating our own console.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class ConsoleHelper
{
    private const int ATTACH_PARENT_PROCESS = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    private static bool _ensured;

    /// <summary>
    /// Make sure Console.WriteLine has somewhere to go. Reattaches std streams
    /// after AttachConsole so writes actually reach the parent terminal.
    /// Safe to call more than once.
    /// </summary>
    public static void EnsureConsole()
    {
        if (_ensured) return;
        _ensured = true;

        bool haveConsole = GetConsoleWindow() != IntPtr.Zero;
        if (!haveConsole)
            haveConsole = AttachConsole(ATTACH_PARENT_PROCESS) || AllocConsole();

        if (!haveConsole) return;

        // After (re)attaching, rebind Console's cached stdout/stderr writers to
        // the real console handles so output isn't swallowed.
        try
        {
            var stdout = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(stdout);
            var stderr = new StreamWriter(Console.OpenStandardError()) { AutoFlush = true };
            Console.SetError(stderr);
        }
        catch { /* best effort */ }
    }
}
