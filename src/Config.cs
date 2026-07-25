using System.Text.Json;
using System.Text.Json.Serialization;

namespace BtAutoConnect;

/// <summary>
/// Strongly-typed JSON config model for config.json.
/// </summary>
public sealed class Config
{
    [JsonPropertyName("devices")]
    public List<DeviceConfig> Devices { get; set; } = new();

    [JsonPropertyName("scanIntervalSeconds")]
    public int ScanIntervalSeconds { get; set; } = 5;

    [JsonPropertyName("dropRetryWindowSeconds")]
    public int DropRetryWindowSeconds { get; set; } = 8;

    [JsonPropertyName("toothTrayCliPath")]
    public string ToothTrayCliPath { get; set; } = "";

    [JsonPropertyName("logPath")]
    public string LogPath { get; set; } = "";

    // --- Static helpers ------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        ReadCommentHandling      = JsonCommentHandling.Skip,
        AllowTrailingCommas      = true,
        PropertyNameCaseInsensitive = true,
        WriteIndented            = true,
    };

    public static Config Load(string path)
    {
        var text = File.ReadAllText(path);
        return JsonSerializer.Deserialize<Config>(text, JsonOpts)
            ?? throw new InvalidOperationException($"Config at {path} parsed to null.");
    }

    /// <summary>
    /// Persist the current config to disk (pretty-printed). Writes to a temp
    /// file first then swaps it in, so a crash mid-write can't corrupt the file.
    /// </summary>
    public void Save(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(this, JsonOpts);
        var tmp  = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    // --- Watch-list helpers (used by the tray UI) ---------------------------

    /// <summary>
    /// Find the config entry that watches the given paired device, matching by
    /// address first (survives renames) then by exact name. Null if not watched.
    /// </summary>
    public DeviceConfig? FindWatch(Bluetooth.DeviceStatus dev)
    {
        var hit = Devices.FirstOrDefault(d =>
            d.AddressParsed is ulong a && a == dev.Address);
        if (hit != null) return hit;

        return Devices.FirstOrDefault(d =>
            string.IsNullOrWhiteSpace(d.Address) &&
            string.Equals(d.Name, dev.Name, StringComparison.Ordinal));
    }

    public bool IsWatched(Bluetooth.DeviceStatus dev) => FindWatch(dev) != null;

    /// <summary>
    /// Turn auto-connect on for a paired device: add a config entry stamped with
    /// its address and best-guess kind. No-op if already watched.
    /// </summary>
    public void AddWatch(Bluetooth.DeviceStatus dev)
    {
        if (IsWatched(dev)) return;
        Devices.Add(new DeviceConfig
        {
            Name    = dev.Name,
            Address = Bluetooth.FormatAddress(dev.Address),
            Kind    = dev.GuessedKind,
        });
    }

    /// <summary>Turn auto-connect off for a paired device. No-op if not watched.</summary>
    public void RemoveWatch(Bluetooth.DeviceStatus dev)
    {
        var hit = FindWatch(dev);
        if (hit != null) Devices.Remove(hit);
    }

    public static void WriteExample(string path)
    {
        var example = new Config
        {
            Devices = new()
            {
                new DeviceConfig { Name = "Bose QC Ultra Earbuds" },
                new DeviceConfig { Name = "Soundcore Space A40",   Kind = "audio" },
                new DeviceConfig { Name = "Wireless Controller",   Kind = "hid"   },
            },
            ScanIntervalSeconds    = 5,
            DropRetryWindowSeconds = 8,
        };
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(example, JsonOpts));
    }
}

public sealed class DeviceConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>Optional MAC. Wins over <see cref="Name"/> when present.</summary>
    [JsonPropertyName("address")]
    public string? Address { get; set; }

    /// <summary>"audio" (default) or "hid".</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = "audio";

    public string KindNormalized =>
        (Kind ?? "audio").Trim().ToLowerInvariant();

    public ulong? AddressParsed => Bluetooth.ParseAddress(Address);
}
