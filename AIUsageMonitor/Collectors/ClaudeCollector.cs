using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsageMonitor.Collectors;

/// <summary>
/// Reads Claude Code usage. Primary source: the same OAuth usage endpoint the
/// CLI's /usage command uses, authenticated with the token Claude Code already
/// stores locally. Fallback: token totals estimated from transcript JSONL files
/// in ~/.claude/projects for the current 5-hour window.
/// </summary>
public static class ClaudeCollector
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>True when exact percentages need the user to sign in (see LoginWindow).</summary>
    public static bool NeedsLogin { get; private set; }

    /// <summary>Back-off: don't hit the usage API again before this time (set on 429).</summary>
    private static DateTimeOffset _skipApiUntil = DateTimeOffset.MinValue;

    /// <summary>Last successful API reading — shown during transient failures.</summary>
    private static ToolUsage? _lastGood;
    private static DateTimeOffset _lastGoodAt;

    public static async Task<ToolUsage> CollectAsync()
    {
        try
        {
            var fromApi = await TryApiAsync();
            if (fromApi != null)
            {
                _lastGood = fromApi;
                _lastGoodAt = DateTimeOffset.Now;
                return fromApi;
            }
        }
        catch (Exception ex)
        {
            Debug("exception: " + ex.Message);
        }

        // Transient failure (timeout, 429 back-off, …) with a valid session:
        // keep showing the last known percentages instead of dropping to the
        // token estimate. Only a real sign-in problem clears them.
        if (!NeedsLogin && _lastGood != null && DateTimeOffset.Now - _lastGoodAt < TimeSpan.FromHours(6))
        {
            return new ToolUsage
            {
                Name = _lastGood.Name,
                Primary = _lastGood.Primary,
                Weekly = _lastGood.Weekly,
                Detail = _lastGood.Detail + $"\nlast update {_lastGoodAt:HH:mm} — retrying…",
                IsEstimate = true,
            };
        }

        return CollectFromTranscripts();
    }

    private static string? _lastDebugMsg;

    private static void Debug(string msg)
    {
        if (msg == _lastDebugMsg) return; // don't grow the log on a repeating failure
        _lastDebugMsg = msg;
        try
        {
            var p = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AIUsageMonitor", "claude_api_debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss} {msg}\n");
        }
        catch { }
    }

    // ---- OAuth usage endpoint -------------------------------------------

    private static async Task<ToolUsage?> TryApiAsync()
    {
        if (DateTimeOffset.UtcNow < _skipApiUntil) return null;

        // Prefer this app's own login (right-click → Claude login…), which can
        // refresh itself; fall back to the token Claude Code stores on disk.
        string? token = await ClaudeAuth.GetAccessTokenAsync() ?? ReadCliToken();
        if (string.IsNullOrEmpty(token)) { Debug("no token available"); NeedsLogin = true; return null; }

        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/api/oauth/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Add("anthropic-beta", "oauth-2025-04-20");
        req.Headers.UserAgent.ParseAdd("AIUsageMonitor/1.0");

        using var resp = await Http.SendAsync(req);
        var bodyText = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            int status = (int)resp.StatusCode;
            Debug($"HTTP {status}: {bodyText[..Math.Min(300, bodyText.Length)]}");

            // Don't hammer the endpoint after a rate-limit response.
            if (status == 429) _skipApiUntil = DateTimeOffset.UtcNow.AddMinutes(10);

            // Exact bars need a working token: explicit rejection, or any
            // failure while the user hasn't signed the widget in yet.
            NeedsLogin = status is 401 or 403 || !ClaudeAuth.HasStoredLogin;
            return null;
        }
        NeedsLogin = false;

        using var body = JsonDocument.Parse(bodyText);
        var root = body.RootElement;

        var fiveHour = ParseWindow(root, "five_hour");
        var sevenDay = ParseWindow(root, "seven_day");

        if (fiveHour == null && sevenDay == null) return null;

        // Any further windows (e.g. model-specific weekly limits like Opus or
        // Fable) go into the tooltip, whatever their key names are.
        var detail = "Claude Code · live";
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Name is "five_hour" or "seven_day") continue;
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var extra = ParseWindow(root, prop.Name);
            if (extra?.Percent is { } ep)
                detail += $"\n{prop.Name.Replace('_', ' ')}: {ep:0.#}% used  {extra.ResetText}";
        }

        return new ToolUsage
        {
            Name = "CC",
            Primary = fiveHour,
            Weekly = sevenDay,
            Detail = detail,
        };
    }

    /// <summary>Access token from Claude Code's own credentials file (read-only; may be stale).</summary>
    private static string? ReadCliToken()
    {
        try
        {
            var credPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", ".credentials.json");
            if (!File.Exists(credPath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(credPath));
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth)) return null;
            // expiresAt on disk is often stale (Claude Code refreshes lazily),
            // so always attempt the call and let a 401 trigger the fallback.
            return oauth.TryGetProperty("accessToken", out var t) ? t.GetString() : null;
        }
        catch { return null; }
    }

    private static LimitInfo? ParseWindow(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;

        double? pct = w.TryGetProperty("utilization", out var u) && u.ValueKind == JsonValueKind.Number
            ? u.GetDouble() : null;

        DateTimeOffset? resets = null;
        if (w.TryGetProperty("resets_at", out var ra))
        {
            if (ra.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(ra.GetString(), out var dto))
                resets = dto;
            else if (ra.ValueKind == JsonValueKind.Number)
                resets = DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64());
        }

        return pct == null && resets == null ? null : new LimitInfo { Percent = pct, ResetsAt = resets };
    }

    // ---- Transcript fallback --------------------------------------------

    private static ToolUsage CollectFromTranscripts()
    {
        try
        {
            var projectsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".claude", "projects");
            if (!Directory.Exists(projectsDir))
                return new ToolUsage { Name = "CC", StatusText = "n/a", Detail = "Claude: no data" };

            var cutoff = DateTime.UtcNow.AddHours(-5);
            long tokens = 0;
            var seenIds = new HashSet<string>();

            var files = Directory.EnumerateFiles(projectsDir, "*.jsonl", SearchOption.AllDirectories)
                .Select(f => new FileInfo(f))
                .Where(f => f.LastWriteTimeUtc > cutoff);

            foreach (var file in files)
            {
                using var fs = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var sr = new StreamReader(fs);
                string? line;
                while ((line = sr.ReadLine()) != null)
                {
                    if (!line.Contains("\"usage\"")) continue;
                    try
                    {
                        using var doc = JsonDocument.Parse(line);
                        var root = doc.RootElement;

                        if (root.TryGetProperty("timestamp", out var ts)
                            && DateTimeOffset.TryParse(ts.GetString(), out var when)
                            && when.UtcDateTime < cutoff)
                            continue;

                        if (!root.TryGetProperty("message", out var msg)) continue;
                        if (!msg.TryGetProperty("usage", out var usage)) continue;

                        // The same assistant message can appear in multiple files.
                        if (msg.TryGetProperty("id", out var id) && id.GetString() is { } mid
                            && !seenIds.Add(mid))
                            continue;

                        tokens += GetLong(usage, "input_tokens") + GetLong(usage, "output_tokens");
                    }
                    catch (JsonException) { }
                }
            }

            if (NeedsLogin)
            {
                return new ToolUsage
                {
                    Name = "CC",
                    StatusText = "login",
                    Detail = "Claude Code — sign-in needed for 5h/weekly bars\n" +
                             "double-click the bar or right-click → \"Claude login…\"\n" +
                             $"(meanwhile: ~{tokens:N0} non-cached tokens in last 5h)",
                    IsEstimate = true,
                };
            }

            return new ToolUsage
            {
                Name = "CC",
                StatusText = FormatTokens(tokens),
                Detail = $"Claude Code (estimate)\n{tokens:N0} non-cached tokens in last 5h\n(usage API unreachable)",
                IsEstimate = true,
            };
        }
        catch (Exception ex)
        {
            return new ToolUsage { Name = "CC", StatusText = "err", Detail = "Claude: " + ex.Message };
        }
    }

    private static long GetLong(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    private static string FormatTokens(long t) => t switch
    {
        >= 1_000_000 => $"{t / 1_000_000.0:0.0}M",
        >= 1_000 => $"{t / 1_000.0:0}k",
        _ => t.ToString(),
    };
}
