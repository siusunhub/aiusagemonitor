using System.IO;
using System.Management;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AIUsageMonitor.Collectors;

/// <summary>
/// Antigravity usage via the local Antigravity language server. The server's
/// RetrieveUserQuotaSummary RPC returns per-model-group 5-hour and weekly
/// limits (the same data the IDE shows). Access requires the CSRF token from
/// the language_server process command line and one of its listening ports.
/// Fallback when Antigravity isn't running: conversation activity.
/// </summary>
public static class AntigravityCollector
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        // The language server uses a self-signed cert on 127.0.0.1.
        ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
    })
    { Timeout = TimeSpan.FromSeconds(6) };

    private const string RpcBody =
        """{"metadata":{"ideName":"antigravity","extensionName":"antigravity","locale":"en"}}""";

    private static int _cachedPort;
    private static string? _cachedToken;

    public static async Task<ToolUsage> CollectAsync()
    {
        try
        {
            var live = await TryLocalApiAsync();
            if (live != null) return live;
        }
        catch
        {
            // fall through to activity fallback
        }
        return CollectActivity();
    }

    // ---- local language-server API ----------------------------------------

    private static async Task<ToolUsage?> TryLocalApiAsync()
    {
        // Try the cached endpoint first; rediscover once if it stopped working.
        if (_cachedPort != 0 && _cachedToken != null)
        {
            var cached = await QueryQuotaAsync(_cachedPort, _cachedToken);
            if (cached != null) return cached;
            _cachedPort = 0;
            _cachedToken = null;
        }

        foreach (var (pid, token) in await Task.Run(FindLanguageServers))
        {
            foreach (var port in NetInterop.GetListeningPorts(pid))
            {
                var usage = await QueryQuotaAsync(port, token);
                if (usage != null)
                {
                    _cachedPort = port;
                    _cachedToken = token;
                    return usage;
                }
            }
        }
        return null;
    }

    private static List<(int Pid, string Token)> FindLanguageServers()
    {
        var result = new List<(int, string)>();
        using var searcher = new ManagementObjectSearcher(
            "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='language_server_windows_x64.exe'");
        foreach (var obj in searcher.Get())
        {
            var cmd = obj["CommandLine"] as string ?? "";
            if (!cmd.Contains("antigravity", StringComparison.OrdinalIgnoreCase)) continue;
            var m = Regex.Match(cmd, @"--csrf_token[=\s]+([a-f0-9\-]+)", RegexOptions.IgnoreCase);
            if (!m.Success) continue;
            result.Add((Convert.ToInt32(obj["ProcessId"]), m.Groups[1].Value));
        }
        return result;
    }

    private static async Task<ToolUsage?> QueryQuotaAsync(int port, string token)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                $"https://127.0.0.1:{port}/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary")
            {
                Content = new StringContent(RpcBody, Encoding.UTF8, "application/json"),
            };
            req.Headers.Add("X-Codeium-Csrf-Token", token);
            req.Headers.Add("Connect-Protocol-Version", "1");

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("response", out var response)) return null;
            if (!response.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return null;

            LimitInfo? primary = null, weekly = null;
            var detail = new StringBuilder("Antigravity · live");

            bool first = true;
            foreach (var group in groups.EnumerateArray())
            {
                string name = group.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                var g5h = ParseBucket(group, "5h");
                var gwk = ParseBucket(group, "weekly");

                // The bars show the Gemini group (first group as returned);
                // other groups (Claude/GPT) go into the tooltip.
                if (first)
                {
                    primary = g5h;
                    weekly = gwk;
                    first = false;
                }
                detail.Append($"\n{name}: 5h {Fmt(g5h)} · wk {Fmt(gwk)} used");
            }

            if (primary == null && weekly == null) return null;

            return new ToolUsage
            {
                Name = "AG",
                Primary = primary,
                Weekly = weekly,
                Detail = detail.ToString(),
            };
        }
        catch
        {
            return null;
        }
    }

    private static LimitInfo? ParseBucket(JsonElement group, string window)
    {
        if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var b in buckets.EnumerateArray())
        {
            if (!b.TryGetProperty("window", out var w) || w.GetString() != window) continue;

            double? usedPct = b.TryGetProperty("remainingFraction", out var rf) && rf.ValueKind == JsonValueKind.Number
                ? (1.0 - rf.GetDouble()) * 100.0
                : null;
            DateTimeOffset? resets = b.TryGetProperty("resetTime", out var rt)
                && DateTimeOffset.TryParse(rt.GetString(), out var dto) ? dto : null;

            return usedPct == null && resets == null ? null : new LimitInfo { Percent = usedPct, ResetsAt = resets };
        }
        return null;
    }

    private static string Fmt(LimitInfo? l) => l?.Percent is { } p ? $"{p:0.#}%" : "?";

    // ---- fallback: conversation activity ------------------------------------

    private static ToolUsage CollectActivity()
    {
        try
        {
            var convDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".gemini", "antigravity-cli", "conversations");

            if (!Directory.Exists(convDir))
                return new ToolUsage { Name = "AG", StatusText = "n/a", Detail = "Antigravity: no data folder" };

            var today = DateTime.Now.Date;
            var dbs = Directory.EnumerateFiles(convDir, "*.db")
                .Select(f => new FileInfo(f))
                .ToList();

            int todayCount = dbs.Count(f => f.LastWriteTime >= today);
            var last = dbs.Count > 0 ? dbs.Max(f => f.LastWriteTime) : (DateTime?)null;

            var detail = "Antigravity (activity only)\nstart Antigravity to see 5h/weekly quota bars";
            if (last is { } l)
                detail += $"\nlast activity: {l:ddd HH:mm}";

            return new ToolUsage
            {
                Name = "AG",
                StatusText = todayCount > 0 ? $"{todayCount} today" : "idle",
                Detail = detail,
                IsEstimate = true,
            };
        }
        catch (Exception ex)
        {
            return new ToolUsage { Name = "AG", StatusText = "err", Detail = "Antigravity: " + ex.Message };
        }
    }
}
