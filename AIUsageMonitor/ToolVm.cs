using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using AIUsageMonitor.Collectors;

namespace AIUsageMonitor;

public sealed class ToolVm : INotifyPropertyChanged
{
    private const double BarFullWidth = 52;

    /// <summary>Color thresholds (% used), set from Config at startup.
    /// Green below YellowAt, amber from YellowAt to RedAt, red above RedAt.</summary>
    public static double YellowAt { get; set; } = 70;
    public static double RedAt { get; set; } = 90;

    /// <summary>Display % remaining (100 = free, full green bar) instead of % used.
    /// Colors always follow the used %, so their meaning flips with the scale.</summary>
    public static bool ShowRemaining { get; set; }

    /// <summary>Compact mode: draw circular gauges instead of long bars.</summary>
    public static bool CompactCircles { get; set; }

    /// <summary>Show the 5-hour reset countdown (both display modes).</summary>
    public static bool ShowResetTime { get; set; } = true;

    private const double RingSize = 24;
    private const double RingThickness = 4;

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

    // circular gauges (compact mode)
    public Geometry Ring1Geometry { get; private set; } = Geometry.Empty;
    public Brush Ring1Brush { get; private set; } = Brushes.Gray;
    public string Ring1CenterText { get; private set; } = "";
    public Geometry Ring2Geometry { get; private set; } = Geometry.Empty;
    public Brush Ring2Brush { get; private set; } = Brushes.Gray;
    public string Ring2CenterText { get; private set; } = "";

    // shown instead of the two rows when no percentages exist
    public string FallbackText { get; private set; } = "…";

    public string Tooltip { get; private set; } = "loading…";

    public Visibility Visibility => _show ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RowsVisibility => _hasRows && !CompactCircles ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CircleVisibility => _hasRows && CompactCircles ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FallbackVisibility => _hasRows ? Visibility.Collapsed : Visibility.Visible;
    public Visibility SeparatorVisibility => _showSeparator ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResetVisibility => _hasRows && ShowResetTime ? Visibility.Visible : Visibility.Collapsed;

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
            (Ring1Geometry, Ring1Brush, Ring1CenterText) = RingFor(u.Primary, tilde);
            (Ring2Geometry, Ring2Brush, Ring2CenterText) = RingFor(u.Weekly, tilde);
            // When the weekly limit is exhausted it is the binding one — show its
            // reset instead of the 5h countdown. Also fall back to weekly when
            // no short window exists (e.g. Codex weekly-only mode).
            bool weeklyExhausted = u.Weekly?.Percent is { } wp && wp >= 100;
            Row1ResetText = CountdownFor(weeklyExhausted
                ? u.Weekly?.ResetsAt ?? u.Primary?.ResetsAt
                : u.Primary?.ResetsAt ?? u.Weekly?.ResetsAt);
        }
        else
        {
            FallbackText = tilde + (u.StatusText ?? "–");
            Row1ResetText = "";
        }

        var lines = new List<string> { u.Detail };
        string TipFor(LimitInfo l) => l.Percent is { } p
            ? (ShowRemaining ? $"{100 - p:0.#}% left" : $"{p:0.#}% used")
            : "?";
        if (u.Primary != null)
            lines.Add($"5-hour: {TipFor(u.Primary)}  {u.Primary.ResetText}");
        if (u.Weekly != null)
            lines.Add($"weekly: {TipFor(u.Weekly)}  {u.Weekly.ResetText}");
        Tooltip = string.Join("\n", lines.Where(l => !string.IsNullOrWhiteSpace(l)));

        Raise();
    }

    private static (string, double, Brush) RowFor(LimitInfo? limit, string tilde)
    {
        if (limit?.Percent is not { } p)
            return ("–", 0, Brushes.DimGray);
        double shown = ShowRemaining ? 100 - p : p;
        return ($"{tilde}{shown:0}%", Math.Clamp(shown, 0, 100) / 100.0 * BarFullWidth, BrushFor(p));
    }

    private static (Geometry, Brush, string) RingFor(LimitInfo? limit, string tilde)
    {
        if (limit?.Percent is not { } p)
            return (Geometry.Empty, Brushes.DimGray, "–");
        double shown = ShowRemaining ? 100 - p : p;
        return (BuildArc(shown), BrushFor(p), $"{tilde}{shown:0}");
    }

    /// <summary>Progress arc from 12 o'clock, clockwise, filling <paramref name="percent"/> of the ring.</summary>
    private static Geometry BuildArc(double percent)
    {
        percent = Math.Clamp(percent, 0, 100);
        double radius = (RingSize - RingThickness) / 2;
        double c = RingSize / 2;
        if (percent <= 0) return Geometry.Empty;
        if (percent >= 99.99) return new EllipseGeometry(new Point(c, c), radius, radius);

        double sweep = percent / 100.0 * 360.0;
        Point start = OnCircle(c, radius, -90);
        Point end = OnCircle(c, radius, -90 + sweep);
        var fig = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        fig.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, sweep > 180,
            SweepDirection.Clockwise, isStroked: true));
        var geo = new PathGeometry();
        geo.Figures.Add(fig);
        geo.Freeze();
        return geo;
    }

    private static Point OnCircle(double c, double r, double deg)
    {
        double rad = deg * Math.PI / 180.0;
        return new Point(c + r * Math.Cos(rad), c + r * Math.Sin(rad));
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
        percentUsed > RedAt ? Red
        : percentUsed >= YellowAt ? Yellow
        : Green;
}
