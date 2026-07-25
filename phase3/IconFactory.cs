using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace BtAutoConnect;

/// <summary>
/// Draws the tray icon at runtime so the app ships as a single binary with no
/// embedded .ico asset. A blue disc means "connected to something right now",
/// grey means "watching / idle". The glyph is the Bluetooth rune.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IconFactory
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    // The Bluetooth rune as a single polyline in a 0..1 unit square (y grows
    // downward): left-upper -> right-lower -> spine bottom -> spine top ->
    // right-upper -> left-lower. The two diagonals cross on the vertical spine.
    private static readonly PointF[] Rune =
    {
        new(0.35f, 0.30f),
        new(0.65f, 0.70f),
        new(0.50f, 0.85f),
        new(0.50f, 0.15f),
        new(0.65f, 0.30f),
        new(0.35f, 0.70f),
    };

    /// <summary>
    /// Create a fresh tray Icon. The caller owns it and must Dispose the
    /// previous one (see <see cref="Destroy"/>) before swapping — GetHicon
    /// allocates an unmanaged handle that GC won't reclaim on its own.
    /// </summary>
    public static Icon Create(bool connected)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            var disc = connected
                ? Color.FromArgb(0, 120, 215)     // Windows accent blue
                : Color.FromArgb(120, 120, 120);  // idle grey
            using (var brush = new SolidBrush(disc))
                g.FillEllipse(brush, 1, 1, size - 2, size - 2);

            using var pen = new Pen(Color.White, 2.2f)
            {
                StartCap = LineCap.Round,
                EndCap   = LineCap.Round,
                LineJoin = LineJoin.Round,
            };

            // Scale the rune into a padded box centred on the disc.
            const float pad = 7f;
            float span = size - 2 * pad;
            var pts = new PointF[Rune.Length];
            for (int i = 0; i < Rune.Length; i++)
                pts[i] = new PointF(pad + Rune[i].X * span, pad + Rune[i].Y * span);
            g.DrawLines(pen, pts);
        }

        IntPtr h = bmp.GetHicon();
        // Clone into a managed Icon so we can free the raw handle immediately and
        // hand back something with a normal .Dispose lifetime.
        using var tmp = Icon.FromHandle(h);
        var icon = (Icon)tmp.Clone();
        DestroyIcon(h);
        return icon;
    }

    public static void Destroy(Icon? icon)
    {
        try { icon?.Dispose(); } catch { /* ignore */ }
    }
}
