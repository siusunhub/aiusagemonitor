using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace AIUsageMonitor.Collectors;

/// <summary>
/// Antigravity CLI (agy) usage. agy keeps its Google OAuth token in Windows
/// Credential Manager (target "gemini:antigravity"); with it, the same cloud
/// quota endpoints the CLI uses return grouped 5-hour/weekly limits or per-model
/// quota when Gemini is absent from the summary. The token is read-only here: never logged, never written, and sent
/// only to googleapis.com. We don't refresh it (that needs agy's own OAuth
/// client) — agy refreshes it whenever it runs, so an idle CLI shows grayed
/// last-known values.
/// </summary>
public static class AntigravityCliCollector
{
    // Note: When called "AG" only, it refers to Antigravity CLI (agy) code.
    private const string Name = "AG";
    private const string Endpoint = "https://daily-cloudcode-pa.googleapis.com/v1internal:retrieveUserQuotaSummary";
    // Older client versions receive a partial summary with the Gemini group omitted.
    private const string UserAgent = "Antigravity/2.24.0 (windows; amd64)";
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
                PrimaryLabel = _lastGood.PrimaryLabel,
                Weekly = _lastGood.Weekly,
                Detail = _lastGood.Detail +
                         $"\nlast update {_lastGoodAt:HH:mm} — retry #{_consecutiveFailures}\n⚠ {reason}",
                IsEstimate = true,
                FetchedAt = _lastGoodAt,
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

    private const string CodeAssistEndpoint = "https://daily-cloudcode-pa.googleapis.com/v1internal:loadCodeAssist";
    private static readonly string[] UserInfoEndpoints =
    {
        "https://openidconnect.googleapis.com/v1/userinfo",
        "https://www.googleapis.com/oauth2/v3/userinfo",
    };

    private static string? _cachedProfileToken;
    private static string? _cachedEmail;
    private static string? _cachedTier;

    private static async Task<(string? Email, string? Tier)> GetUserProfileAsync(string accessToken)
    {
        if (_cachedProfileToken == accessToken && !string.IsNullOrEmpty(_cachedEmail))
            return (_cachedEmail, _cachedTier);

        try
        {
            var emailTask = FetchAccountEmailAsync(accessToken);
            var tierTask = FetchUserTierAsync(accessToken);

            await Task.WhenAll(emailTask, tierTask);
            var email = await emailTask;
            var tier = await tierTask;

            if (!string.IsNullOrEmpty(email))
            {
                _cachedProfileToken = accessToken;
                _cachedEmail = email;
                _cachedTier = tier;
            }

            return (email ?? _cachedEmail, tier ?? _cachedTier);
        }
        catch
        {
            return (_cachedEmail, _cachedTier);
        }
    }

    private static async Task<string?> FetchAccountEmailAsync(string accessToken)
    {
        foreach (var endpoint in UserInfoEndpoints)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, endpoint);
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                req.Headers.UserAgent.ParseAdd(UserAgent);

                using var resp = await Http.SendAsync(req);
                if (!resp.IsSuccessStatusCode) continue;

                using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
                if (doc.RootElement.TryGetProperty("email", out var em) && em.ValueKind == JsonValueKind.String)
                {
                    var email = em.GetString();
                    if (!string.IsNullOrEmpty(email)) return email;
                }
            }
            catch
            {
                // try next endpoint
            }
        }
        return null;
    }

    private static async Task<string?> FetchUserTierAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, CodeAssistEndpoint)
            {
                Content = new StringContent(
                    """{"cloudaicompanionProject":"","metadata":{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}}""",
                    Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Add("X-Goog-Api-Client", "gl-node/22");
            req.Headers.Add("Client-Metadata",
                """{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}""");

            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var root = doc.RootElement;
            if (root.TryGetProperty("paidTier", out var pt) && pt.TryGetProperty("name", out var ptn) && ptn.ValueKind == JsonValueKind.String)
                return ptn.GetString();

            if (root.TryGetProperty("allowedTiers", out var tiers) && tiers.ValueKind == JsonValueKind.Array)
            {
                string? fallback = null;
                foreach (var t in tiers.EnumerateArray())
                {
                    string id = t.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
                    string name = t.TryGetProperty("name", out var nProp) ? nProp.GetString() ?? "" : "";
                    if (!string.IsNullOrEmpty(name))
                    {
                        if (id != "free-tier") return name;
                        fallback ??= name;
                    }
                }
                return fallback;
            }
        }
        catch
        {
            // best-effort
        }
        return null;
    }

    private static async Task<(ToolUsage? Usage, string Reason)> QueryAsync(string accessToken)
    {
        try
        {
            var profileTask = GetUserProfileAsync(accessToken);

            using var req = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.UserAgent.ParseAdd(UserAgent);
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

            var (userEmail, userTier) = await profileTask;
            var header = !string.IsNullOrEmpty(userEmail)
                ? (!string.IsNullOrEmpty(userTier) ? $"{userEmail} · {userTier}" : userEmail)
                : "Antigravity CLI · live";

            LimitInfo? primary = null, weekly = null;
            string primaryLabel = "5-hour";
            var detail = new StringBuilder(header);
            bool first = true;
            bool hasGeminiQuota = false;
            foreach (var group in groups.EnumerateArray())
            {
                string name = group.TryGetProperty("displayName", out var dn) ? dn.GetString() ?? "" : "";
                var g5h = AntigravityCollector.ParseBucket(group, "5h");
                var gwk = AntigravityCollector.ParseBucket(group, "weekly");
                bool isGemini = name.Contains("gemini", StringComparison.OrdinalIgnoreCase)
                    || (group.TryGetProperty("buckets", out var buckets) && buckets.ValueKind == JsonValueKind.Array
                        && buckets.EnumerateArray().Any(b => b.TryGetProperty("bucketId", out var id)
                            && id.ValueKind == JsonValueKind.String
                            && id.GetString()!.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)));

                // Prefer Gemini regardless of group order; keep other pools in the tooltip.
                if ((first || isGemini) && (g5h != null || gwk != null))
                {
                    primary = g5h;
                    weekly = gwk;
                    first = false;
                    hasGeminiQuota |= isGemini;
                }
                detail.Append($"\n{name}: 5h {AntigravityCollector.Fmt(g5h)} · wk {AntigravityCollector.Fmt(gwk)} used");
            }

            if (!hasGeminiQuota)
            {
                // The summary may now contain only Claude/GPT. Gemini quota is
                // still exposed per model, without an explicit weekly/5h window.
                var gemini = await FetchGeminiQuotaAsync(accessToken);
                if (gemini != null)
                {
                    primary = gemini;
                    weekly = null;
                    primaryLabel = "Gemini model quota";
                    detail.Append($"\nGemini models (most used): {AntigravityCollector.Fmt(gemini)} used");
                    detail.Append("\nGemini quota window duration and weekly limit are not supplied by the model endpoint.");
                }
                else
                {
                    detail.Append("\nGemini quota unavailable; bars show the returned model group above.");
                }
            }

            if (primary == null && weekly == null) return (null, "unexpected response");

            return (new ToolUsage { Name = Name, Primary = primary, PrimaryLabel = primaryLabel, Weekly = weekly, Detail = detail.ToString() }, "");
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

    private static async Task<LimitInfo?> FetchGeminiQuotaAsync(string accessToken)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post,
                "https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels")
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            req.Headers.Add("X-Goog-Api-Client", "gl-node/22");
            req.Headers.Add("Client-Metadata",
                """{"ideType":"IDE_UNSPECIFIED","platform":"PLATFORM_UNSPECIFIED","pluginType":"GEMINI"}""");
            using var resp = await Http.SendAsync(req);
            if ((int)resp.StatusCode == 429) _skipUntil = DateTimeOffset.UtcNow.AddMinutes(10);
            if (!resp.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            return ParseGeminiModelQuota(doc.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // Keep the available summary when the model endpoint fails.
        }
    }

    internal static LimitInfo? ParseGeminiModelQuota(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Object)
            return null;

        LimitInfo? mostUsed = null;
        foreach (var model in models.EnumerateObject())
        {
            if (!model.Name.StartsWith("gemini-", StringComparison.OrdinalIgnoreCase)
                || model.Value.ValueKind != JsonValueKind.Object
                || !model.Value.TryGetProperty("quotaInfo", out var quota) || quota.ValueKind != JsonValueKind.Object
                || !quota.TryGetProperty("resetTime", out var rt) || rt.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(rt.GetString(), out var reset)) continue;

            // Proto3 JSON may omit a zero remainingFraction.
            double remaining = 0;
            if (quota.TryGetProperty("remainingFraction", out var rf)
                && (rf.ValueKind != JsonValueKind.Number || !rf.TryGetDouble(out remaining))) continue;
            if (!double.IsFinite(remaining) || remaining < 0 || remaining > 1) continue;
            double used = (1 - remaining) * 100;
            if (mostUsed == null || used > mostUsed.Percent)
                mostUsed = new LimitInfo { Percent = used, ResetsAt = reset };
        }
        return mostUsed;
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
