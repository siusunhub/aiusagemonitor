namespace AIUsageMonitor.Collectors;

/// <summary>One rate-limit window (e.g. 5-hour or weekly).</summary>
public sealed class LimitInfo
{
    public double? Percent { get; init; }
    public DateTimeOffset? ResetsAt { get; init; }

    public string ResetText =>
        ResetsAt is { } r ? $"resets {r.ToLocalTime():ddd HH:mm}" : "";
}

/// <summary>Usage snapshot for one tool.</summary>
public sealed class ToolUsage
{
    public required string Name { get; init; }

    /// <summary>Short-window limit (typically 5 hours). Null if unknown.</summary>
    public LimitInfo? Primary { get; init; }

    /// <summary>Weekly limit. Null if unknown.</summary>
    public LimitInfo? Weekly { get; init; }

    /// <summary>Shown instead of a percentage when no limit data exists (e.g. "3 sessions").</summary>
    public string? StatusText { get; init; }

    /// <summary>Extra lines for the tooltip.</summary>
    public string Detail { get; init; } = "";

    /// <summary>True when data came from a stale/estimated source.</summary>
    public bool IsEstimate { get; init; }
}
