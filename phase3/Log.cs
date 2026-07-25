namespace BtAutoConnect;

/// <summary>
/// Tee timestamped lines to both the console (when not in -Background) and
/// a rotating log file. Mirrors the behavior of Write-Stamp in the
/// PowerShell prototype.
/// </summary>
public sealed class Log
{
    private readonly string _path;
    private readonly long   _maxBytes;
    private readonly int    _keep;
    private readonly bool   _quiet;

    private static readonly object _gate = new();

    /// <summary>Absolute path of the log file (used by the tray "Open log" action).</summary>
    public string Path => _path;

    public Log(string path, long maxBytes, int keep, bool quiet)
    {
        _path     = path;
        _maxBytes = maxBytes;
        _keep     = keep;
        _quiet    = quiet;

        // Fully qualified: this class exposes a 'Path' property that would
        // otherwise shadow System.IO.Path here.
        var dir = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public void Info(string msg)  => Write(msg, ConsoleColor.Gray);
    public void Ok(string msg)    => Write(msg, ConsoleColor.Green);
    public void Warn(string msg)  => Write(msg, ConsoleColor.Yellow);
    public void Quiet(string msg) => Write(msg, ConsoleColor.DarkGray);
    public void Bad(string msg)   => Write(msg, ConsoleColor.Red);
    public void Note(string msg)  => Write(msg, ConsoleColor.Cyan);
    public void Hint(string msg)  => Write(msg, ConsoleColor.DarkYellow);

    public void Write(string msg, ConsoleColor color)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";

        if (!_quiet)
        {
            lock (_gate)
            {
                var prev = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = color;
                    Console.WriteLine(line);
                }
                finally
                {
                    Console.ForegroundColor = prev;
                }
            }
        }

        // File logging: rotate-if-needed, then append. Failures are silent
        // so a locked log file never crashes the watchdog.
        try
        {
            lock (_gate)
            {
                Rotate();
                File.AppendAllText(_path, line + Environment.NewLine);
            }
        }
        catch { /* don't care */ }
    }

    private void Rotate()
    {
        if (!File.Exists(_path)) return;
        long size;
        try { size = new FileInfo(_path).Length; } catch { return; }
        if (size < _maxBytes) return;

        // Drop the oldest backup, shift the rest up, current becomes .1.
        var oldest = $"{_path}.{_keep}";
        if (File.Exists(oldest))
        {
            try { File.Delete(oldest); } catch { }
        }

        for (int i = _keep - 1; i >= 1; i--)
        {
            var src = $"{_path}.{i}";
            var dst = $"{_path}.{i + 1}";
            if (File.Exists(src))
            {
                try { File.Move(src, dst, overwrite: true); } catch { }
            }
        }

        try { File.Move(_path, $"{_path}.1", overwrite: true); } catch { }
    }
}
