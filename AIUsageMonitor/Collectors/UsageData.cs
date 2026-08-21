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

    /// <summary>
    /// Returns true if 5h or Weekly limit is >= 100% and the earliest applicable reset time is more than 1 minute away.
    /// </summary>
    public bool ShouldPauseCheck(DateTimeOffset? now = null)
    {
        var current = now ?? DateTimeOffset.UtcNow;
        DateTimeOffset? nextReset = null;

        if (Primary?.Percent is { } pp && pp >= 100 && Primary.ResetsAt is { } pr && pr > current)
        {
            nextReset = pr;
        }

        if (Weekly?.Percent is { } wp && wp >= 100 && Weekly.ResetsAt is { } wr && wr > current)
        {
            if (nextReset == null || wr < nextReset.Value)
                nextReset = wr;
        }

        if (nextReset is { } r)
        {
            return (r - current) > TimeSpan.FromMinutes(1);
        }

        return false;
    }
}

