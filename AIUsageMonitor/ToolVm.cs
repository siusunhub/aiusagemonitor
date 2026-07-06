using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Collectors;

namespace AIUsageMonitor;

public sealed class ToolVm : INotifyPropertyChanged
{
    private const double BarFullWidth = 52;

    /// <summary>Color thresholds (% used), set from Config at startup.</summary>
    public static double YellowAt { get; set; } = 75;
    public static double RedAt { get; set; } = 95;

    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x4C, 0xC3, 0x8A));
    private static readonly Brush Yellow = new SolidColorBrush(Color.FromRgb(0xF5, 0xC8, 0x42));
    private static readonly Brush Red = new SolidColorBrush(Color.FromRgb(0xF2, 0x55, 0x5A));

    private bool _show = true;
    private bool _hasRows;
    private bool _showSeparator;
    private ToolUsage? _last;

    public string Name { get; }

    // 5-hour row
    public string Row1Text { get; private set; } = "";
    public double Row1BarWidth { get; private set; }
    public Brush Row1Brush { get; private set; } = Brushes.Gray;
    public string Row1ResetText { get; private set; } = "";

    // weekly row
    public string Row2Text { get; private set; } = "";
    public double Row2BarWidth { get; private set; }
    public Brush Row2Brush { get; private set; } = Brushes.Gray;

    // shown instead of the two rows when no percentages exist
    public string FallbackText { get; private set; } = "…";

    public string Tooltip { get; private set; } = "loading…";

    public Visibility Visibility => _show ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowsVisibility => _hasRows ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FallbackVisibility => _hasRows ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SeparatorVisibility => _showSeparator ? Visibility.Visible : Visibility.Collapsed;

    public bool IsShown => _show;

    public ToolVm(string name) => Name = name;

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetVisible(bool show)
    {
        _show = show;
        Raise();
    }

    /// <summary>Leading "|" divider — hidden on the first visible segment.</summary>
    public void SetSeparator(bool show)
    {
        _showSeparator = show;
        Raise();
    }

    /// <summary>Recompute the reset countdown between data refreshes.</summary>
    public void Tick()
    {
        if (_last != null) Update(_last);
    }

    public void Update(ToolUsage u)
    {
        _last = u;
        var tilde = u.IsEstimate ? "~" : "";
        _hasRows = u.Primary?.Percent != null || u.Weekly?.Percent != null;

        if (_hasRows)
        {
            (Row1Text, Row1BarWidth, Row1Brush) = RowFor(u.Primary, tilde);
            (Row2Text, Row2BarWidth, Row2Brush) = RowFor(u.Weekly, tilde);
            Row1ResetText = CountdownFor(u.Primary?.ResetsAt);
        }
        else
        {
            FallbackText = tilde + (u.StatusText ?? "–");
            Row1ResetText = "";
        }

        var lines = new List<string> { u.Detail };
        if (u.Primary != null)
            lines.Add($"5-hour: {(u.Primary.Percent is { } pp ? $"{pp:0.#}% used" : "?")}  {u.Primary.ResetText}");
        if (u.Weekly != null)
            lines.Add($"weekly: {(u.Weekly.Percent is { } wp ? $"{wp:0.#}% used" : "?")}  {u.Weekly.ResetText}");
        Tooltip = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));

        Raise();
    }

    private static (string, double, Brush) RowFor(LimitInfo? limit, string tilde)
    {
        if (limit?.Percent is not { } p)
            return ("–", 0, Brushes.DimGray);
        return ($"{tilde}{p:0}%", Math.Clamp(p, 0, 100) / 100.0 * BarFullWidth, BrushFor(p));
    }

    /// <summary>Compact time-to-reset, e.g. "1h20m" / "45m" / "2d16h".</summary>
    private static string CountdownFor(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } r) return "";
        var left = r - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "";

        return left.TotalDays >= 1
            ? $"{(int)left.TotalDays}d{left.Hours}h"
            : left.TotalHours >= 1
                ? $"{(int)left.TotalHours}h{left.Minutes:00}m"
                : $"{Math.Max(1, left.Minutes)}m";
    }

    private void Raise() =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    private static Brush BrushFor(double percentUsed) =>
        percentUsed >= RedAt ? Red
        : percentUsed > YellowAt ? Yellow
        : Green;
}
