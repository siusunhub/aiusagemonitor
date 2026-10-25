using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AIUsageMonitor.Collectors;

public static class GrokCollector
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public static async Task<ToolUsage> CollectAsync()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("GROK_HOME");
            if (string.IsNullOrWhiteSpace(home))
                home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok");
            var authPath = Path.Combine(home, "auth.json");
            if (!File.Exists(authPath)) return LoginRequired();

            using var auth = JsonDocument.Parse(await File.ReadAllTextAsync(authPath));
            if (auth.RootElement.ValueKind != JsonValueKind.Object) return LoginRequired();

            JsonElement credentials = default;
            foreach (var entry in auth.RootElement.EnumerateObject())
            {
                if (entry.Name.StartsWith("https://auth.x.ai::", StringComparison.Ordinal) &&
                    entry.Name.Length > "https://auth.x.ai::".Length &&
                    entry.Value.ValueKind == JsonValueKind.Object)
                {
                    credentials = entry.Value;
                    break;
                }
            }
            if (credentials.ValueKind == JsonValueKind.Undefined)
                auth.RootElement.TryGetProperty("https://accounts.x.ai/sign-in", out credentials);

            if (credentials.ValueKind != JsonValueKind.Object ||
                !credentials.TryGetProperty("key", out var key) || key.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(key.GetString()) ||
                !credentials.TryGetProperty("user_id", out var userId) || userId.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(userId.GetString()))
                return LoginRequired();

            using var request = new HttpRequestMessage(HttpMethod.Get,
                "https://cli-chat-proxy.grok.com/v1/billing?format=credits");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.GetString());
            request.Headers.Add("X-XAI-Token-Auth", "xai-grok-cli");
            request.Headers.Add("x-userid", userId.GetString());
            request.Headers.Add("x-grok-client-version", "1.0.70");
            using var response = await Http.SendAsync(request);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                return LoginRequired();
            if (!response.IsSuccessStatusCode)
                return new ToolUsage { Name = "GK", StatusText = "err", Detail = $"Grok CLI: HTTP {(int)response.StatusCode}" };

            using var billing = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            return ParseBilling(billing.RootElement);
        }
        catch
        {
            return new ToolUsage { Name = "GK", StatusText = "err", Detail = "Grok CLI: unable to read usage. Run Grok CLI to check your connection and sign-in." };
        }
    }

    internal static ToolUsage ParseBilling(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("config", out var config) || config.ValueKind != JsonValueKind.Object ||
            !config.TryGetProperty("currentPeriod", out var period) || period.ValueKind != JsonValueKind.Object ||
            !period.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String ||
            !period.TryGetProperty("end", out var end) || end.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(end.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var resetsAt))
            return new ToolUsage { Name = "GK", StatusText = "n/a", Detail = "Grok CLI: billing period unavailable." };

        bool isWeekly = type.GetString() == "USAGE_PERIOD_TYPE_WEEKLY";
        bool hasPercent = config.TryGetProperty("creditUsagePercent", out var usage);
        if (!hasPercent)
            return new ToolUsage { Name = "GK", StatusText = "n/a", Detail = "Grok CLI: usage percentage unavailable." };
        double percent = 0;
        if (hasPercent &&
            (usage.ValueKind != JsonValueKind.Number || !usage.TryGetDouble(out percent) ||
             !double.IsFinite(percent) || percent < 0))
            return new ToolUsage { Name = "GK", StatusText = "n/a", Detail = "Grok CLI: invalid usage percentage." };

        if (!isWeekly)
            return new ToolUsage
            {
                Name = "GK",
                StatusText = "n/a",
                Detail = (hasPercent ? $"Grok CLI: {percent.ToString("0.#", CultureInfo.InvariantCulture)}% used\n" : "Grok CLI: ") +
                         "Billing period is not weekly; weekly quota unavailable.",
            };

        return new ToolUsage
        {
            Name = "GK",
            Weekly = new LimitInfo { Percent = percent, ResetsAt = resetsAt },
            Detail = "Grok CLI · live\nWeekly credit allowance",
        };
    }

    private static ToolUsage LoginRequired() => new()
    {
        Name = "GK",
        StatusText = "login",
        Detail = "Grok CLI: sign-in required. Run Grok CLI to sign in or renew your session.",
    };
}
