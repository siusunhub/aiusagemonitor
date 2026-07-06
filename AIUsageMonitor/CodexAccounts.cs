using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AIUsageMonitor;

public sealed record CodexAccount(string Alias, string? Email, bool IsMaster, bool IsActive, string StoreFile);

/// <summary>
/// Multiple Codex accounts by swapping ~/.codex/auth.json with stored copies.
///
/// Store layout under %APPDATA%\AIUsageMonitor\codex\:
///   auth_master.json  — backup of the original CLI login ("base" account)
///   &lt;alias&gt;.json      — each additional account (alias = A-Z a-z 0-9 only)
///   accounts.json     — metadata (base alias, which account is active)
///
/// Switching copies the target file over ~/.codex/auth.json; before that, the
/// current auth.json is synced back into the active account's store file so
/// refreshed tokens are never lost. Adding an account runs `codex login`.
/// </summary>
public static class CodexAccounts
{
    public static readonly Regex AliasRegex = new("^[A-Za-z0-9]{1,32}$");
    private static readonly string[] ReservedAliases = { "accounts", "master" };

    private static string CodexAuthPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "auth.json");

    private static string StoreDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AIUsageMonitor", "codex");

    private static string MasterFile => Path.Combine(StoreDir, "auth_master.json");
    private static string MetaFile => Path.Combine(StoreDir, "accounts.json");

    private sealed class Meta
    {
        public string? MasterAlias { get; set; }
        public string ActiveAlias { get; set; } = "master";
    }

    /// <summary>Menu label: "alias (email)", but no duplicate when the alias IS the email.</summary>
    public static string DisplayName(CodexAccount a)
    {
        string name = string.IsNullOrEmpty(a.Email) || a.Alias == a.Email
            ? a.Alias
            : $"{a.Alias}  ({a.Email})";
        return a.IsMaster ? name + "  [base]" : name;
    }

    public static bool IsValidAlias(string alias) =>
        AliasRegex.IsMatch(alias) && !ReservedAliases.Contains(alias.ToLowerInvariant())
        && !alias.Equals("auth_master", StringComparison.OrdinalIgnoreCase);

    // ---- listing -----------------------------------------------------------

    public static List<CodexAccount> List()
    {
        var meta = LoadMeta();
        var list = new List<CodexAccount>();

        var (curEmail, curId) = InfoOf(CodexAuthPath);

        string masterSrc = File.Exists(MasterFile) ? MasterFile : CodexAuthPath;
        var (mEmail, mId) = InfoOf(masterSrc);
        string masterAlias = meta.MasterAlias ?? mEmail ?? "master";

        var aliases = new List<(string Alias, string? Email, string? Id, string File)>();
        if (Directory.Exists(StoreDir))
        {
            foreach (var f in Directory.GetFiles(StoreDir, "*.json").OrderBy(f => f))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Equals("auth_master", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("accounts", StringComparison.OrdinalIgnoreCase)) continue;
                var (e, i) = InfoOf(f);
                aliases.Add((name, e, i, f));
            }
        }

        // Which stored account matches the auth.json Codex currently uses?
        string active = "master";
        if (curId != null)
        {
            var matches = new List<string>();
            if (mId == curId) matches.Add("master");
            matches.AddRange(aliases.Where(a => a.Id == curId).Select(a => a.Alias));
            if (matches.Count > 0)
                active = matches.Contains(meta.ActiveAlias) ? meta.ActiveAlias : matches[0];
        }

        list.Add(new CodexAccount(masterAlias, mEmail ?? curEmail, true, active == "master", MasterFile));
        foreach (var a in aliases)
            list.Add(new CodexAccount(a.Alias, a.Email, false, active == a.Alias, a.File));
        return list;
    }

    // ---- switching ----------------------------------------------------------

    public static void Switch(CodexAccount target)
    {
        Directory.CreateDirectory(StoreDir);
        EnsureMasterBackup();

        var accounts = List();
        var active = accounts.FirstOrDefault(a => a.IsActive);
        if (active != null && active.IsMaster == target.IsMaster && active.Alias == target.Alias)
            return; // already active

        // Keep the active account's freshest tokens before swapping it out.
        if (active != null && File.Exists(CodexAuthPath))
            File.Copy(CodexAuthPath, active.StoreFile, true);

        if (!File.Exists(target.StoreFile))
            throw new InvalidOperationException($"Stored account file missing: {target.StoreFile}");

        File.Copy(target.StoreFile, CodexAuthPath, true);

        var meta = LoadMeta();
        meta.ActiveAlias = target.IsMaster ? "master" : target.Alias;
        SaveMeta(meta);
    }

    // ---- add / remove / rename ----------------------------------------------

    /// <summary>Runs `codex login` for a NEW account and stores it under the alias. Slow — browser sign-in.</summary>
    public static async Task AddAccountAsync(string alias)
    {
        if (!IsValidAlias(alias))
            throw new InvalidOperationException("Alias must be 1-32 English letters/numbers only.");
        var targetFile = Path.Combine(StoreDir, alias + ".json");
        if (File.Exists(targetFile))
            throw new InvalidOperationException($"Alias \"{alias}\" is already used.");

        Directory.CreateDirectory(StoreDir);
        EnsureMasterBackup();

        // Preserve the currently active account, then clear auth.json so
        // `codex login` starts a fresh sign-in instead of reusing the session.
        var active = List().FirstOrDefault(a => a.IsActive);
        var rollback = Path.Combine(StoreDir, "auth_rollback.tmp");
        if (File.Exists(CodexAuthPath))
        {
            if (active != null) File.Copy(CodexAuthPath, active.StoreFile, true);
            File.Copy(CodexAuthPath, rollback, true);
            File.Delete(CodexAuthPath);
        }

        try
        {
            bool ok = await RunCodexLoginAsync();
            if (!ok || !File.Exists(CodexAuthPath))
                throw new InvalidOperationException("Codex login did not complete (cancelled or timed out).");

            File.Copy(CodexAuthPath, targetFile, true);
            var meta = LoadMeta();
            meta.ActiveAlias = alias; // the fresh login is now the active account
            SaveMeta(meta);
        }
        catch
        {
            // Restore whatever was active before the attempt.
            if (File.Exists(rollback)) File.Copy(rollback, CodexAuthPath, true);
            throw;
        }
        finally
        {
            if (File.Exists(rollback)) File.Delete(rollback);
        }
    }

    public static void Remove(CodexAccount account)
    {
        if (account.IsMaster)
            throw new InvalidOperationException("The base account cannot be removed.");

        // Never leave Codex signed in to an account we're deleting.
        if (account.IsActive)
        {
            var master = List().First(a => a.IsMaster);
            Switch(master);
        }
        if (File.Exists(account.StoreFile)) File.Delete(account.StoreFile);
    }

    public static void Rename(CodexAccount account, string newAlias)
    {
        if (!IsValidAlias(newAlias))
            throw new InvalidOperationException("Alias must be 1-32 English letters/numbers only.");

        var meta = LoadMeta();
        if (account.IsMaster)
        {
            meta.MasterAlias = newAlias;
            SaveMeta(meta);
            return;
        }

        var newFile = Path.Combine(StoreDir, newAlias + ".json");
        if (File.Exists(newFile))
            throw new InvalidOperationException($"Alias \"{newAlias}\" is already used.");
        File.Move(account.StoreFile, newFile);
        if (meta.ActiveAlias == account.Alias) meta.ActiveAlias = newAlias;
        SaveMeta(meta);
    }

    // ---- per-account usage ----------------------------------------------------

    public sealed record CodexUsage(double? FiveHourPct, DateTimeOffset? FiveHourReset,
        double? WeeklyPct, DateTimeOffset? WeeklyReset, string? Error)
    {
        public static CodexUsage Fail(string msg) => new(null, null, null, null, msg);
    }

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // Codex CLI's public OAuth client + token endpoint (verified in the binary),
    // used to refresh inactive accounts — the CLI only refreshes the active one.
    private const string OAuthClientId = "app_EMoamEEZ73f0CkXaXp7hrann";
    private const string OAuthTokenUrl = "https://auth.openai.com/oauth/token";

    /// <summary>Usage/reset for any stored account, refreshing its token if stale.</summary>
    public static async Task<CodexUsage> FetchUsageAsync(CodexAccount account)
    {
        try
        {
            string path = account.IsActive && File.Exists(CodexAuthPath) ? CodexAuthPath : account.StoreFile;
            if (!File.Exists(path)) return CodexUsage.Fail("no stored login");

            var (access, refresh, accountId) = ReadTokens(path);
            if (access == null) return CodexUsage.Fail("no token");

            var usage = await QueryUsageAsync(access, accountId);
            if (usage != null) return usage;

            // Access token rejected/expired — refresh once and retry.
            if (refresh == null) return CodexUsage.Fail("sign-in expired");
            var newAccess = await RefreshTokensAsync(path, refresh);
            if (newAccess == null) return CodexUsage.Fail("sign-in expired — re-add this account");

            usage = await QueryUsageAsync(newAccess, accountId);
            return usage ?? CodexUsage.Fail("usage query failed");
        }
        catch (Exception ex)
        {
            return CodexUsage.Fail(ex.Message);
        }
    }

    private static (string? Access, string? Refresh, string? AccountId) ReadTokens(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        if (!doc.RootElement.TryGetProperty("tokens", out var t)) return (null, null, null);
        return (
            t.TryGetProperty("access_token", out var a) ? a.GetString() : null,
            t.TryGetProperty("refresh_token", out var r) ? r.GetString() : null,
            t.TryGetProperty("account_id", out var i) ? i.GetString() : null);
    }

    private static async Task<CodexUsage?> QueryUsageAsync(string accessToken, string? accountId)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://chatgpt.com/backend-api/wham/usage");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        if (!string.IsNullOrEmpty(accountId)) req.Headers.Add("chatgpt-account-id", accountId);
        req.Headers.UserAgent.ParseAdd("codex-cli");

        using var resp = await Http.SendAsync(req);
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("rate_limit", out var rl)) return null;

        (double? Pct, DateTimeOffset? Reset) Window(string key)
        {
            if (!rl.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return (null, null);
            double? pct = w.TryGetProperty("used_percent", out var up) && up.ValueKind == JsonValueKind.Number
                ? up.GetDouble() : null;
            DateTimeOffset? reset = w.TryGetProperty("reset_at", out var ra) && ra.ValueKind == JsonValueKind.Number
                ? DateTimeOffset.FromUnixTimeSeconds(ra.GetInt64()) : null;
            return (pct, reset);
        }

        var p = Window("primary_window");
        var s = Window("secondary_window");
        return new CodexUsage(p.Pct, p.Reset, s.Pct, s.Reset, null);
    }

    /// <summary>Refresh an account's tokens and persist them back into its file. Returns the new access token.</summary>
    private static async Task<string?> RefreshTokensAsync(string path, string refreshToken)
    {
        var payload = JsonSerializer.Serialize(new
        {
            client_id = OAuthClientId,
            grant_type = "refresh_token",
            refresh_token = refreshToken,
            scope = "openid profile email",
        });
        using var resp = await Http.PostAsync(OAuthTokenUrl,
            new StringContent(payload, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode) return null;

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        string? access = root.TryGetProperty("access_token", out var a) ? a.GetString() : null;
        if (access == null) return null;

        // Persist rotated tokens immediately or the old refresh token dies with them.
        var json = JsonNode.Parse(File.ReadAllText(path))!;
        var tokens = json["tokens"] ??= new JsonObject();
        tokens["access_token"] = access;
        if (root.TryGetProperty("refresh_token", out var r) && r.GetString() is { } nr)
            tokens["refresh_token"] = nr;
        if (root.TryGetProperty("id_token", out var idt) && idt.GetString() is { } nid)
            tokens["id_token"] = nid;
        json["last_refresh"] = DateTimeOffset.UtcNow.ToString("o");
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return access;
    }

    // ---- internals -----------------------------------------------------------

    private static void EnsureMasterBackup()
    {
        Directory.CreateDirectory(StoreDir);
        if (!File.Exists(MasterFile) && File.Exists(CodexAuthPath))
            File.Copy(CodexAuthPath, MasterFile);
    }

    private static async Task<bool> RunCodexLoginAsync()
    {
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c codex login",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return false;

        // codex login opens the browser and waits for the OAuth callback.
        var exited = await Task.Run(() => p.WaitForExit(300_000));
        if (!exited)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            return false;
        }
        return p.ExitCode == 0;
    }

    /// <summary>Email (from the id_token JWT) and account id inside an auth.json file.</summary>
    private static (string? Email, string? Id) InfoOf(string path)
    {
        try
        {
            if (!File.Exists(path)) return (null, null);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty("tokens", out var t)) return (null, null);

            string? id = t.TryGetProperty("account_id", out var a) ? a.GetString() : null;
            string? email = null;
            if (t.TryGetProperty("id_token", out var idt) && idt.GetString() is { } jwt)
            {
                var parts = jwt.Split('.');
                if (parts.Length >= 2)
                {
                    var payload = parts[1].Replace('-', '+').Replace('_', '/');
                    payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
                    using var pd = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)));
                    email = pd.RootElement.TryGetProperty("email", out var e) ? e.GetString() : null;
                }
            }
            return (email, id);
        }
        catch { return (null, null); }
    }

    private static Meta LoadMeta()
    {
        try
        {
            if (File.Exists(MetaFile))
                return JsonSerializer.Deserialize<Meta>(File.ReadAllText(MetaFile)) ?? new Meta();
        }
        catch { }
        return new Meta();
    }

    private static void SaveMeta(Meta meta)
    {
        Directory.CreateDirectory(StoreDir);
        File.WriteAllText(MetaFile, JsonSerializer.Serialize(meta, new JsonSerializerOptions { WriteIndented = true }));
    }
}
