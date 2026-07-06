using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace AIUsageMonitor;

public sealed class Config
{
    /// <summary>Gap in device pixels between the bar's right edge and the tray-icon area.</summary>
    public int OffsetX { get; set; } = 8;

    public int RefreshSeconds { get; set; } = 60;

    public bool ShowClaude { get; set; } = true;
    public bool ShowCodex { get; set; } = true;
    public bool ShowAntigravity { get; set; } = true;

    /// <summary>Whether the taskbar bar is visible (tray icon can always bring it back).</summary>
    public bool BarVisible { get; set; } = true;

    /// <summary>Which taskbar to dock on: 0 = primary, 1+ = secondary taskbars left-to-right.</summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>Bar turns amber at or above this % used (green below).</summary>
    public double YellowAtPercent { get; set; } = 70;

    /// <summary>Bar turns red above this % used. Set above 100 to disable red.</summary>
    public double RedAtPercent { get; set; } = 90;

    private static string PathFor() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIUsageMonitor", "config.json");

    public static Config Load()
    {
        try
        {
            var p = PathFor();
            if (File.Exists(p))
                return JsonSerializer.Deserialize<Config>(File.ReadAllText(p)) ?? new Config();
        }
        catch { }
        return new Config();
    }

    public void Save()
    {
        try
        {
            var p = PathFor();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(p)!);
            File.WriteAllText(p, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    // ---- Start with Windows ----------------------------------------------

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "AIUsageMonitor";

    public static bool IsAutostartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) != null;
    }

    public static void SetAutostart(bool enable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable)
            key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else
            key.DeleteValue(RunValue, throwOnMissingValue: false);
    }
}
