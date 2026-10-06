using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Collectors;

/// <summary>
/// Antigravity CLI (agy) usage. agy keeps its Google OAuth token in Windows
/// Credential Manager (target "gemini:antigravity"); with it, the same cloud
/// quota endpoint the CLI uses returns the per-model-group 5-hour and weekly
/// limits. The token is read-only here: never logged, never written, and sent
/// only to googleapis.com. We don't refresh it (that needs agy's own OAuth
/// client) — agy refreshes it whenever it runs, so an idle CLI shows grayed
/// last-known values.
/// </summary>
public static class AntigravityCliCollector
{
    private const string Name = "AG CLI";
    private const string Endpoint = "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    private static readonly string[] CredentialTargets =
    {
        "gemini:antigravity",
        "LegacyGeneric:target=gemini:antigravity",
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>Back-off: don't hit the endpoint again before this time (set on 429).</summary>
    private static DateTimeOffset _skipUntil = DateTimeOffset.MinValue;

    /// <summary>Last successful reading — shown (grayed) during failures.</summary>
    private static ToolUsage? _lastGood;
    private static DateTimeOffset _lastGoodAt;
    private static int _consecutiveFailures;

    public static async Task<ToolUsage> CollectAsync()
    {
        try
        {
            var token = ReadToken();
            if (token == null)
            {
                return new ToolUsage
                {
                    Name = Name,
                    StatusText = "off",
                    Detail = "Antigravity CLI not signed in — run agy once to sign in",
                };
            }

            if (token.Expiry is { } exp && exp <= DateTimeOffset.UtcNow)
                return Failed("token expired — use agy to refresh it");

            if (DateTimeOffset.UtcNow < _skipUntil)
                return Failed("rate limited (HTTP 429), backing off");

            var (usage, reason) = await QueryAsync(token.AccessToken);
            if (usage != null)
            {
                _consecutiveFailures = 0;
                _lastGood = usage;
                _lastGoodAt = DateTimeOffset.Now;
                return usage;
            }
            return Failed(reason);
        }
        catch (Exception ex)
        {
            return Failed("error: " + ex.Message);
        }
    }

    /// <summary>Failure: keep the last good numbers grayed with the retry count and reason.</summary>
    private static ToolUsage Failed(string reason)
    {
        _consecutiveFailures++;

        if (_lastGood != null && DateTimeOffset.Now - _lastGoodAt < TimeSpan.FromHours(6))
        {
            return new ToolUsage
            {
                Name = Name,
                Primary = _lastGood.Primary,
                Weekly = _lastGood.Weekly,
                Detail = _lastGood.Detail +
                         $"\nlast update {_lastGoodAt:HH:mm} — retry #{_consecutiveFailures}\n⚠ {reason}",
                IsEstimate = true,
            };
        }

        return new ToolUsage
        {
            Name = Name,
            StatusText = "n/a",
            Detail = $"Antigravity CLI: {reason}\nretry #{_consecutiveFailures}",
        };
    }

    // ---- cloud quota endpoint ----------------------------------------------

    private static async Task<(ToolUsage? Usage, string Reason)> QueryAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.UserAgent.ParseAdd("antigravity/1.0 windows/amd64");
            // Same client headers agy sends; the endpoint is picky about its callers.
            req.Headers.Add("X-Goog-Api-Client", "gl-node/22");
            req.Headers.Add("Client-Metadata",
                """{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}""");

            using var resp = await Http.SendAsync(req);
            int status = (int)resp.StatusCode;
            if (!resp.IsSuccessStatusCode)
            {
                if (status == 429) _skipUntil = DateTimeOffset.UtcNow.AddMinutes(10);
                return (null, status switch
                {
                    401 or 403 => $"signed out (HTTP {status}) — sign in again with agy",
                    429 => "rate limited (HTTP 429), backing off 10 min",
                    >= 500 => $"server error (HTTP {status})",
                    _ => $"client error (HTTP {status})",
                });
            }

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            // The local RPC wraps the payload in "response"; the cloud endpoint may not.
            if (root.TryGetProperty("response", out var wrapped)) root = wrapped;

            if (!root.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                return (null, "unexpected response");

            LimitInfo? primary = null, weekly = null;
            var detail = new StringBuilder("Antigravity CLI · live");
            bool first = true;
            foreach (var group in groups.EnumerateArray())
            {
                string name = group.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                var g5h = AntigravityCollector.ParseBucket(group, "5h");
                var gwk = AntigravityCollector.ParseBucket(group, "weekly");

                // Bars show the first group (Gemini); the rest go into the tooltip.
                if (first)
                {
                    primary = g5h;
                    weekly = gwk;
                    first = false;
                }
                detail.Append($"\n{name}: 5h {AntigravityCollector.Fmt(g5h)} · wk {AntigravityCollector.Fmt(gwk)} used");
            }

            if (primary == null && weekly == null) return (null, "unexpected response");

            return (new ToolUsage { Name = Name, Primary = primary, Weekly = weekly, Detail = detail.ToString() }, "");
        }
        catch (TaskCanceledException)
        {
            return (null, "timeout (10s)");
        }
        catch (HttpRequestException ex)
        {
            return (null, "connection error: " + (ex.InnerException?.Message ?? ex.Message));
        }
        catch (JsonException)
        {
            return (null, "unexpected response");
        }
    }

    // ---- Windows Credential Manager ----------------------------------------

    private sealed record CliToken(string AccessToken, DateTimeOffset? Expiry);

    private static CliToken? ReadToken()
    {
        foreach (var target in CredentialTargets)
        {
            var blob = ReadCredentialBlob(target);
            if (blob == null) continue;

            // agy writes the JSON as UTF-8; accept UTF-16 as well.
            foreach (var enc in new[] { Encoding.UTF8, Encoding.Unicode })
            {
                try
                {
                    using var doc = JsonDocument.Parse(enc.GetString(blob));
                    var root = doc.RootElement;
                    var tok = root.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.Object ? t : root;
                    if (!tok.TryGetProperty("access_token", out var at) || at.GetString() is not { Length: > 0 } access)
                        continue;

                    DateTimeOffset? expiry = null;
                    if (tok.TryGetProperty("expiry", out var ex))
                    {
                        if (ex.ValueKind == JsonValueKind.String
                            && DateTimeOffset.TryParse(ex.GetString(), System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.AssumeUniversal, out var dto))
                            expiry = dto;
                        else if (ex.ValueKind == JsonValueKind.Number)
                            expiry = DateTimeOffset.FromUnixTimeMilliseconds(ex.GetInt64());
                    }
                    return new CliToken(access, expiry);
                }
                catch (JsonException) { }
            }
        }
        return null;
    }

    private static byte[]? ReadCredentialBlob(string target)
    {
        const uint CRED_TYPE_GENERIC = 1;
        if (!CredRead(target, CRED_TYPE_GENERIC, 0, out var ptr)) return null;
        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            CredFree(ptr);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint reservedFlag, out IntPtr credential);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
