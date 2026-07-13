using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsageMonitor.Collectors;

/// <summary>
/// Codex usage. Primary source: the same backend endpoint the CLI's /status
/// uses (live, includes usage from all devices), authenticated with the token
/// Codex stores in ~/.codex/auth.json. Fallback: the last rate_limits snapshot
/// in the newest local session rollout file (local-only, can be stale).
/// </summary>
public static class CodexCollector
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<ToolUsage> CollectAsync()
    {
        try
        {
            var live = await TryApiAsync();
            if (live != null) return live;
        }
        catch
        {
            // fall through to local session files
        }
        return CollectFromSessions();
    }

    // ---- live backend endpoint -------------------------------------------

    private static async Task<ToolUsage?> TryApiAsync()
    {
        var authPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".codex", "auth.json");
        if (!File.Exists(authPath)) return null;

        string? accessToken, accountId;
        using (var doc = JsonDocument.Parse(File.ReadAllText(authPath)))
        {
            if (!doc.RootElement.TryGetProperty("tokens", out var tokens)) return null;
            accessToken = tokens.TryGetProperty("access_token", out var at) ? at.GetString() : null;
            accountId = tokens.TryGetProperty("account_id", out var ai) ? ai.GetString() : null;
        }
        if (string.IsNullOrEmpty(accessToken)) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrEmpty(accountId)) req.Headers.Add("chatgpt-account-id", accountId);
        req.Headers.UserAgent.ParseAdd("codex-cli");

        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        using var body = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = body.RootElement;
        if (!root.TryGetProperty("rate_limit", out var rl)) return null;

        var (primary, weekly) = AssignWindows(
            ParseApiWindow(rl, "primary_window"),
            ParseApiWindow(rl, "secondary_window"));
        if (primary == null && weekly == null) return null;

        string plan = root.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() ?? "" : "";
        string note = primary == null && weekly != null
            ? "\n(5h limit currently off — Codex counts weekly only)" : "";

        return new ToolUsage
        {
            Name = "CX",
            Primary = primary,
            Weekly = weekly,
            Detail = $"Codex ({plan}) · live{note}",
        };
    }

    private static (LimitInfo? Info, double? Seconds) ParseApiWindow(JsonElement rateLimit, string key)
    {
        if (!rateLimit.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
            return (null, null);

        double? pct = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number
            ? up.GetDouble() : null;
        DateTimeOffset? resets = w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()) : null;
        double? seconds = w.TryGetProperty("limit_window_seconds", out var lw) && lw.ValueKind == JsonValueKind.Number
            ? lw.GetDouble() : null;

        return pct == null && resets == null ? (null, null)
            : (new LimitInfo { Percent = pct, ResetsAt = resets }, seconds);
    }

    // ---- window classification ----------------------------------------------

    /// <summary>A window longer than a day belongs in the weekly slot.</summary>
    private const double LongWindowSeconds = 24 * 3600;

    /// <summary>
    /// Places each window into the 5h or weekly slot by its actual duration
    /// instead of trusting primary/secondary order — Codex sometimes disables
    /// the 5h limit and moves the weekly window into primary. When the
    /// duration is missing, falls back to the traditional key-based mapping.
    /// </summary>
    internal static (LimitInfo? Short, LimitInfo? Long) AssignWindows(
        (LimitInfo? Info, double? Seconds) primary,
        (LimitInfo? Info, double? Seconds) secondary)
    {
        LimitInfo? shortW = null, longW = null;

        void Place((LimitInfo? Info, double? Seconds) w, bool defaultIsShort)
        {
            if (w.Info == null) return;
            bool isLong = w.Seconds is { } s ? s > LongWindowSeconds : !defaultIsShort;
            if (isLong)
            {
                if (longW == null) longW = w.Info; else shortW ??= w.Info;
            }
            else
            {
                if (shortW == null) shortW = w.Info; else longW ??= w.Info;
            }
        }

        Place(primary, defaultIsShort: true);
        Place(secondary, defaultIsShort: false);
        return (shortW, longW);
    }

    // ---- fallback: local session files -------------------------------------

    private static ToolUsage CollectFromSessions()
    {
        try
        {
            var sessionsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex", "sessions");

            if (!Directory.Exists(sessionsDir))
                return new ToolUsage { Name = "CX", StatusText = "n/a", Detail = "Codex: no sessions folder" };

            var files = Directory.EnumerateFiles(sessionsDir, "rollout-*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(5);

            foreach (var file in files)
            {
                var usage = ParseFile(file);
                if (usage != null) return usage;
            }

            return new ToolUsage { Name = "CX", StatusText = "idle", Detail = "Codex: no recent rate-limit data" };
        }
        catch (Exception ex)
        {
            return new ToolUsage { Name = "CX", StatusText = "err", Detail = "Codex: " + ex.Message };
        }
    }

    private static ToolUsage? ParseFile(FileInfo file)
    {
        // Only the tail matters; rate_limits appears on every token_count event.
        const int tailBytes = 512 * 1024;
        string tail;
        using (var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            if (fs.Length > tailBytes) fs.Seek(-tailBytes, SeekOrigin.End);
            using var sr = new StreamReader(fs);
            tail = sr.ReadToEnd();
        }

        var lines = tail.Split('\n');
        for (int i = lines.Length - 1; i >= 0; i--)
        {
            var line = lines[i];
            if (!line.Contains("\"rate_limits\"")) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                if (!doc.RootElement.TryGetProperty("payload", out var payload)) continue;
                if (!payload.TryGetProperty("rate_limits", out var rl)) continue;

                var (primary, weekly) = AssignWindows(
                    ParseWindow(rl, "primary"),
                    ParseWindow(rl, "secondary"));
                string plan = rl.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String
                    ? p.GetString() ?? "" : "";

                // A window whose reset time has passed is back at 0%.
                primary = ZeroIfExpired(primary);
                weekly = ZeroIfExpired(weekly);

                return new ToolUsage
                {
                    Name = "CX",
                    Primary = primary,
                    Weekly = weekly,
                    Detail = $"Codex ({plan}) · offline data from {file.LastWriteTime:ddd HH:mm}\n" +
                             "(live endpoint unreachable — may miss usage from other devices)",
                    IsEstimate = true,
                };
            }
            catch (JsonException)
            {
                // Truncated first line of the tail window — keep scanning.
            }
        }
        return null;
    }

    private static (LimitInfo? Info, double? Seconds) ParseWindow(JsonElement rateLimits, string key)
    {
        if (!rateLimits.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object)
            return (null, null);

        double? pct = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number
            ? up.GetDouble() : null;
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()) : null;
        double? seconds = w.TryGetProperty("window_minutes", out var wm) && wm.ValueKind == JsonValueKind.Number
            ? wm.GetDouble() * 60
            : w.TryGetProperty("limit_window_seconds", out var lw) && lw.ValueKind == JsonValueKind.Number
                ? lw.GetDouble() : null;

        return pct == null && resets == null ? (null, null)
            : (new LimitInfo { Percent = pct, ResetsAt = resets }, seconds);
    }

    private static LimitInfo? ZeroIfExpired(LimitInfo? w) =>
        w?.ResetsAt is { } r && r < DateTimeOffset.UtcNow
            ? new LimitInfo { Percent = 0, ResetsAt = null }
            : w;
}
