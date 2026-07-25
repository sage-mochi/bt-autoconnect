using System.Diagnostics;
using System.Drawing;
using System.Runtime.Versioning;
using System.Windows.Forms;

namespace BtAutoConnect;

/// <summary>
/// A small settings dialog over the same <see cref="Config"/> the tray
/// menu edits. Lets the user set the scan interval and reconnect window, and
/// manage auto-connect / kind for every device in one grid (paired devices plus
/// any configured-but-not-currently-paired entries). On OK it writes the choices
/// back into the passed-in Config; the caller persists and restarts the watchdog.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsForm : Form
{
    private sealed record RowMeta(string Name, ulong Address, bool Paired);

    private readonly Config _config;
    private readonly NumericUpDown _scan  = new();
    private readonly NumericUpDown _drop  = new();
    private readonly DataGridView  _grid  = new();

    public SettingsForm(Config config)
    {
        _config = config;

        Text            = "bt-autoconnect — Settings";
        StartPosition   = FormStartPosition.CenterScreen;
        MinimumSize     = new Size(600, 420);
        Size            = new Size(640, 460);
        ShowInTaskbar   = true;
        Icon            = IconFactory.Create(connected: false);
        Font            = SystemFonts.MessageBoxFont ?? SystemFonts.DefaultFont;

        BuildGrid();
        var top    = BuildTopPanel();
        var bottom = BuildBottomPanel();

        // Add the Fill control first so the Top/Bottom panels claim the edges.
        Controls.Add(_grid);
        Controls.Add(top);
        Controls.Add(bottom);

        PopulateRows();
    }

    // --- Layout --------------------------------------------------------------

    private Control BuildTopPanel()
    {
        var panel = new TableLayoutPanel
        {
            Dock        = DockStyle.Top,
            ColumnCount = 4,
            RowCount    = 1,
            Height      = 44,
            Padding     = new Padding(10, 8, 10, 4),
            AutoSize    = false,
        };

        _scan.Minimum = 1; _scan.Maximum = 60; _scan.Value = Clamp(_config.ScanIntervalSeconds, 1, 60);
        _drop.Minimum = 1; _drop.Maximum = 60; _drop.Value = Clamp(_config.DropRetryWindowSeconds, 1, 60);
        _scan.Width = 60; _drop.Width = 60;

        panel.Controls.Add(new Label { Text = "Scan interval (s):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 0) }, 0, 0);
        panel.Controls.Add(_scan, 1, 0);
        panel.Controls.Add(new Label { Text = "Reconnect window (s):", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(20, 6, 6, 0) }, 2, 0);
        panel.Controls.Add(_drop, 3, 0);
        return panel;
    }

    private Control BuildBottomPanel()
    {
        var panel = new FlowLayoutPanel
        {
            Dock          = DockStyle.Bottom,
            FlowDirection = FlowDirection.RightToLeft,
            Height        = 48,
            Padding       = new Padding(10, 8, 10, 8),
        };

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
        ok.Click += (_, _) => ApplyToConfig();
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };

        var pair = new Button { Text = "Pair a new device…", AutoSize = true, Margin = new Padding(0, 0, 24, 0) };
        pair.Click += (_, _) => OpenBluetoothSettings();

        var refresh = new Button { Text = "Refresh", AutoSize = true };
        refresh.Click += (_, _) => PopulateRows();

        panel.Controls.Add(ok);       // rightmost
        panel.Controls.Add(cancel);
        panel.Controls.Add(pair);
        panel.Controls.Add(refresh);

        AcceptButton = ok;
        CancelButton = cancel;
        return panel;
    }

    private void BuildGrid()
    {
        _grid.Dock                     = DockStyle.Fill;
        _grid.AllowUserToAddRows       = false;
        _grid.AllowUserToDeleteRows    = false;
        _grid.AllowUserToResizeRows    = false;
        _grid.RowHeadersVisible        = false;
        _grid.SelectionMode            = DataGridViewSelectionMode.CellSelect;
        _grid.AutoSizeColumnsMode      = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.EditMode                 = DataGridViewEditMode.EditOnEnter;
        _grid.BackgroundColor          = SystemColors.Window;
        _grid.BorderStyle              = BorderStyle.None;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;

        var auto = new DataGridViewCheckBoxColumn { HeaderText = "Auto-connect", Name = "auto", FillWeight = 20 };
        var name = new DataGridViewTextBoxColumn  { HeaderText = "Device",       Name = "name", ReadOnly = true, FillWeight = 34 };
        var addr = new DataGridViewTextBoxColumn  { HeaderText = "Address",      Name = "addr", ReadOnly = true, FillWeight = 22 };
        var kind = new DataGridViewComboBoxColumn { HeaderText = "Kind",         Name = "kind", FillWeight = 12 };
        kind.Items.Add("audio"); kind.Items.Add("hid");
        kind.FlatStyle = FlatStyle.Flat;
        var stat = new DataGridViewTextBoxColumn  { HeaderText = "Status",       Name = "stat", ReadOnly = true, FillWeight = 12 };

        _grid.Columns.AddRange(auto, name, addr, kind, stat);

        // Commit checkbox/combo edits immediately so OK sees the latest values.
        _grid.CurrentCellDirtyStateChanged += (_, _) =>
        {
            if (_grid.IsCurrentCellDirty)
                _grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
        };
        // A combo cell with an unexpected value would otherwise throw.
        _grid.DataError += (_, e) => e.ThrowException = false;
    }

    // --- Data ----------------------------------------------------------------

    private void PopulateRows()
    {
        _grid.Rows.Clear();

        List<Bluetooth.DeviceStatus> paired;
        try { paired = Bluetooth.EnumerateDevices(); }
        catch { paired = new List<Bluetooth.DeviceStatus>(); }

        // Real paired devices first.
        var shown = new HashSet<ulong>();
        foreach (var d in paired
                     .Where(d => !d.IsLeShadow && !string.IsNullOrWhiteSpace(d.Name))
                     .OrderByDescending(d => d.Connected)
                     .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
        {
            shown.Add(d.Address);
            var watch = _config.FindWatch(d);
            string kind = watch?.KindNormalized ?? d.GuessedKind;
            AddRow(d.Name, d.Address, kind, watched: watch != null,
                   status: d.Connected ? "Connected" : "Paired",
                   paired: true);
        }

        // Configured devices that aren't currently paired (kept so the user can
        // pre-configure or see stale entries).
        foreach (var c in _config.Devices)
        {
            ulong addr = c.AddressParsed ?? 0;
            bool already = (addr != 0 && shown.Contains(addr)) ||
                           paired.Any(p => addr == 0 && string.Equals(p.Name, c.Name, StringComparison.Ordinal));
            if (already) continue;

            AddRow(c.Name, addr, c.KindNormalized, watched: true, status: "Not paired", paired: false);
        }
    }

    private void AddRow(string name, ulong address, string kind, bool watched, string status, bool paired)
    {
        int i = _grid.Rows.Add();
        var row = _grid.Rows[i];
        row.Cells["auto"].Value = watched;
        row.Cells["name"].Value = name;
        row.Cells["addr"].Value = address != 0 ? Bluetooth.FormatAddress(address) : "—";
        row.Cells["kind"].Value = kind == "hid" ? "hid" : "audio";
        row.Cells["stat"].Value = status;
        if (!paired) row.DefaultCellStyle.ForeColor = SystemColors.GrayText;
        row.Tag = new RowMeta(name, address, paired);
    }

    private void ApplyToConfig()
    {
        _config.ScanIntervalSeconds    = (int)_scan.Value;
        _config.DropRetryWindowSeconds = (int)_drop.Value;

        var devices = new List<DeviceConfig>();
        foreach (DataGridViewRow row in _grid.Rows)
        {
            if (row.Tag is not RowMeta meta) continue;
            bool on = row.Cells["auto"].Value is bool b && b;
            if (!on) continue;

            var kind = row.Cells["kind"].Value as string ?? "audio";
            devices.Add(new DeviceConfig
            {
                Name    = meta.Name,
                Address = meta.Address != 0 ? Bluetooth.FormatAddress(meta.Address) : null,
                Kind    = kind == "hid" ? "hid" : "audio",
            });
        }
        _config.Devices = devices;
    }

    // --- Helpers -------------------------------------------------------------

    private static void OpenBluetoothSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true }); }
        catch { /* ignore */ }
    }

    private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
}
