using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Collectors;

/// <summary>
/// GitHub Copilot (CP) usage.
/// Reads live quota snapshots (Chat requests and Code Completions), plan details,
/// and monthly reset timestamps maintained by the GitHub Copilot runtime in:
/// %LOCALAPPDATA%\copilot\copilot-user-cache.json.
/// </summary>
public static class CopilotCollector
{
    private const string Name = "CP";

    private static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "copilot", "copilot-user-cache.json");

    private static string SessionStateDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".copilot", "session-state");

    public static async Task<ToolUsage> CollectAsync()
    {
        try
        {
            var path = CachePath;
            if (!File.Exists(path))
            {
                if (Directory.Exists(SessionStateDir) && Directory.GetDirectories(SessionStateDir).Length > 0)
                {
                    return new ToolUsage
                    {
                        Name = Name,
                        StatusText = "active",
                        Detail = "GitHub Copilot: session active\nUsage cache waiting for runtime update",
                        IsEstimate = true,
                    };
                }

                return new ToolUsage
                {
                    Name = Name,
                    StatusText = "off",
                    Detail = "GitHub Copilot: sign-in required or no active session detected",
                };
            }

            var options = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var doc = await JsonDocument.ParseAsync(stream, options);

            if (!doc.RootElement.TryGetProperty("copilotUserCache", out var cacheObj) ||
                cacheObj.ValueKind != JsonValueKind.Object)
            {
                return new ToolUsage
                {
                    Name = Name,
                    StatusText = "off",
                    Detail = "GitHub Copilot: user cache empty",
                };
            }

            JsonElement activeEntry = default;
            foreach (var prop in cacheObj.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object &&
                    prop.Value.TryGetProperty("response", out var resp) &&
                    resp.ValueKind == JsonValueKind.Object)
                {
                    activeEntry = prop.Value;
                    break;
                }
            }

            if (activeEntry.ValueKind != JsonValueKind.Object)
            {
                return new ToolUsage
                {
                    Name = Name,
                    StatusText = "off",
                    Detail = "GitHub Copilot: no active profile found in cache",
                };
            }

            DateTimeOffset fetchedAt = DateTimeOffset.UtcNow;
            if (activeEntry.TryGetProperty("retrievedAt", out var ret) && ret.ValueKind == JsonValueKind.String &&
                DateTimeOffset.TryParse(ret.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var rat))
            {
                fetchedAt = rat;
            }

            var response = activeEntry.GetProperty("response");
            string login = response.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "";
            string plan = response.TryGetProperty("copilot_plan", out var p) ? p.GetString() ?? "" : "";
            string resetStr = response.TryGetProperty("quota_reset_date_utc", out var r) ? r.GetString() ?? "" : "";

            DateTimeOffset? resetDate = null;
            if (DateTimeOffset.TryParse(resetStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dto))
                resetDate = dto;
            else if (DateTime.TryParse(resetStr, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var dt))
                resetDate = new DateTimeOffset(dt, TimeSpan.Zero);

            LimitInfo? chatInfo = null;
            LimitInfo? compInfo = null;
            double chatEnt = 0, chatRem = 0, chatUsed = 0;
            double compEnt = 0, compRem = 0, compUsed = 0;

            if (response.TryGetProperty("quota_snapshots", out var snapshots) &&
                snapshots.ValueKind == JsonValueKind.Object)
            {
                // Chat quota (primary window)
                if (snapshots.TryGetProperty("chat", out var chatObj) && chatObj.ValueKind == JsonValueKind.Object)
                {
                    chatEnt = chatObj.TryGetProperty("entitlement", out var ce) && ce.TryGetDouble(out var cev) ? cev : 0;
                    chatRem = chatObj.TryGetProperty("remaining", out var cr) && cr.TryGetDouble(out var crv) ? crv : 0;
                    double pctRem = chatObj.TryGetProperty("percent_remaining", out var cpr) && cpr.TryGetDouble(out var cprv) ? cprv : 0;
                    chatUsed = Math.Max(0, 100.0 - pctRem);

                    if (chatEnt > 0)
                    {
                        chatInfo = new LimitInfo
                        {
                            Percent = chatUsed,
                            ResetsAt = resetDate,
                        };
                    }
                }

                // Code completions quota (weekly/monthly window)
                if (snapshots.TryGetProperty("completions", out var compObj) && compObj.ValueKind == JsonValueKind.Object)
                {
                    compEnt = compObj.TryGetProperty("entitlement", out var me) && me.TryGetDouble(out var mev) ? mev : 0;
                    compRem = compObj.TryGetProperty("remaining", out var mr) && mr.TryGetDouble(out var mrv) ? mrv : 0;
                    double pctRem = compObj.TryGetProperty("percent_remaining", out var mpr) && mpr.TryGetDouble(out var mprv) ? mprv : 0;
                    compUsed = Math.Max(0, 100.0 - pctRem);

                    if (compEnt > 0)
                    {
                        compInfo = new LimitInfo
                        {
                            Percent = compUsed,
                            ResetsAt = resetDate,
                        };
                    }
                }
            }

            if (chatInfo == null && compInfo == null)
            {
                return new ToolUsage
                {
                    Name = Name,
                    StatusText = "n/a",
                    Detail = "GitHub Copilot: quota telemetry unavailable",
                };
            }

            string planTitle = !string.IsNullOrEmpty(plan)
                ? char.ToUpper(plan[0]) + plan[1..]
                : "Active";
            string header = !string.IsNullOrEmpty(login)
                ? $"{login} · Copilot {planTitle}"
                : $"GitHub Copilot ({planTitle})";

            var detail = new StringBuilder(header);
            if (chatInfo != null)
                detail.Append($"\nChat: {chatRem:0.#} / {chatEnt:0.#} remaining ({chatUsed:0.#}% used)");
            if (compInfo != null)
                detail.Append($"\nCompletions: {compRem:0.#} / {compEnt:0.#} remaining ({compUsed:0.#}% used)");
            if (resetDate != null)
                detail.Append($"\nMonthly quota reset: {resetDate.Value.ToLocalTime():yyyy-MM-dd HH:mm}");

            return new ToolUsage
            {
                Name = Name,
                Primary = chatInfo,
                PrimaryLabel = "chat",
                Weekly = compInfo,
                Detail = detail.ToString(),
                FetchedAt = fetchedAt,
            };
        }
        catch (Exception ex)
        {
            return new ToolUsage
            {
                Name = Name,
                StatusText = "err",
                Detail = $"GitHub Copilot: {ex.Message}",
            };
        }
    }
}
