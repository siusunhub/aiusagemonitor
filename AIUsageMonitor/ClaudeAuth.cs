using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIUsageMonitor;

/// <summary>
/// Standalone OAuth login for the Claude usage API (same public client the
/// Claude Code CLI uses). Tokens are stored in this app's own file under
/// %APPDATA%\AIUsageMonitor — the CLI's ~/.claude/.credentials.json is never
/// written, so the widget can't break the CLI's login.
/// </summary>
public static class ClaudeAuth
{
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";
    private const string AuthorizeUrl = "https://claude.ai/oauth/authorize";
    private const string TokenUrl = "https://console.anthropic.com/v1/oauth/token";
    private const string RedirectUri = "https://console.anthropic.com/oauth/code/callback";
    private const string Scope = "org:create_api_key user:profile user:inference";

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    private static string TokenPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AIUsageMonitor", "claude_oauth.json");

    public static bool HasStoredLogin => File.Exists(TokenPath);

    public sealed record PendingLogin(string Url, string Verifier, string State);

    private sealed class StoredTokens
    {
        [JsonPropertyName("access_token")] public string AccessToken { get; set; } = "";
        [JsonPropertyName("refresh_token")] public string? RefreshToken { get; set; }
        [JsonPropertyName("expires_at_ms")] public long ExpiresAtUnixMs { get; set; }
    }

    // ---- login flow --------------------------------------------------------

    public static PendingLogin BeginLogin()
    {
        string verifier = RandomUrlSafe(64);
        string challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        string state = RandomUrlSafe(32);

        string url = $"{AuthorizeUrl}?code=true&client_id={ClientId}&response_type=code" +
                     $"&redirect_uri={Uri.EscapeDataString(RedirectUri)}" +
                     $"&scope={Uri.EscapeDataString(Scope)}" +
                     $"&code_challenge={challenge}&code_challenge_method=S256&state={state}";

        var login = new PendingLogin(url, verifier, state);
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        return login;
    }

    /// <summary>Exchange the code the user pasted (format: "code#state") for tokens.</summary>
    public static async Task CompleteLoginAsync(PendingLogin login, string pasted)
    {
        var parts = pasted.Trim().Split('#');
        if (string.IsNullOrWhiteSpace(parts[0]))
            throw new InvalidOperationException("Please paste the code shown in the browser.");

        var payload = JsonSerializer.Serialize(new
        {
            grant_type = "authorization_code",
            code = parts[0],
            state = parts.Length > 1 ? parts[1] : login.State,
            client_id = ClientId,
            redirect_uri = RedirectUri,
            code_verifier = login.Verifier,
        });

        using var resp = await Http.PostAsync(TokenUrl,
            new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException(
                $"Login failed (HTTP {(int)resp.StatusCode}): {body[..Math.Min(200, body.Length)]}");

        SaveTokenResponse(body, previousRefreshToken: null);
    }

    // ---- token access ------------------------------------------------------

    /// <summary>Valid access token from this app's own store, refreshing if needed. Null if not logged in.</summary>
    public static async Task<string?> GetAccessTokenAsync()
    {
        var t = LoadTokens();
        if (t == null) return null;

        if (DateTimeOffset.FromUnixTimeMilliseconds(t.ExpiresAtUnixMs) > DateTimeOffset.UtcNow.AddMinutes(2))
            return t.AccessToken;

        if (string.IsNullOrEmpty(t.RefreshToken)) return t.AccessToken;

        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                grant_type = "refresh_token",
                refresh_token = t.RefreshToken,
                client_id = ClientId,
            });
            using var resp = await Http.PostAsync(TokenUrl,
                new StringContent(payload, Encoding.UTF8, "application/json"));

            if (resp.IsSuccessStatusCode)
            {
                SaveTokenResponse(await resp.Content.ReadAsStringAsync(), previousRefreshToken: t.RefreshToken);
                Log("token refreshed");
                return LoadTokens()?.AccessToken;
            }

            int status = (int)resp.StatusCode;
            Log($"token refresh failed: HTTP {status}");

            // 400/401 = the refresh token itself was rejected → session is truly
            // dead, a new login is required. Anything else (429, 5xx) is
            // transient: keep the session and try the old access token — the
            // usage API will tell us if it has really expired.
            return status is 400 or 401 ? null : t.AccessToken;
        }
        catch (Exception ex)
        {
            // Network hiccup — never drop the session for this.
            Log("token refresh error: " + ex.Message);
            return t.AccessToken;
        }
    }

    private static string? _lastLog;

    private static void Log(string msg)
    {
        if (!App.EnableDebugLog) return;
        if (msg == _lastLog) return;
        _lastLog = msg;
        try
        {
            var p = Path.Combine(Path.GetDirectoryName(TokenPath)!, "claude_api_debug.log");
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.AppendAllText(p, $"{DateTime.Now:HH:mm:ss} [auth] {msg}\n");
        }
        catch { }
    }

    private static StoredTokens? LoadTokens()
    {
        try
        {
            if (!File.Exists(TokenPath)) return null;
            return JsonSerializer.Deserialize<StoredTokens>(File.ReadAllText(TokenPath));
        }
        catch { return null; }
    }

    private static void SaveTokenResponse(string tokenResponseJson, string? previousRefreshToken)
    {
        using var doc = JsonDocument.Parse(tokenResponseJson);
        var r = doc.RootElement;

        long expiresIn = r.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
            ? ei.GetInt64() : 3600;

        var t = new StoredTokens
        {
            AccessToken = r.GetProperty("access_token").GetString()!,
            // A refresh response may omit refresh_token; keep the old one then.
            RefreshToken = r.TryGetProperty("refresh_token", out var rt) && rt.ValueKind == JsonValueKind.String
                ? rt.GetString() : previousRefreshToken,
            ExpiresAtUnixMs = DateTimeOffset.UtcNow.AddSeconds(expiresIn).ToUnixTimeMilliseconds(),
        };

        Directory.CreateDirectory(Path.GetDirectoryName(TokenPath)!);
        File.WriteAllText(TokenPath, JsonSerializer.Serialize(t));
    }

    // ---- PKCE helpers ------------------------------------------------------

    private static string RandomUrlSafe(int bytes)
    {
        var buf = new byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Base64Url(buf);
    }

    private static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
