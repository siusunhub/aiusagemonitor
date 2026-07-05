# AI Usage Monitor

A slim Windows 11 taskbar widget showing current usage of **Claude Code (CC)**,
**Codex (CX)** and **Antigravity CLI (AG)** — TrafficMonitor-style, docked just
left of the tray icons.

```
CC 42%   CX 15% wk 21%   AG 3 today
▂▂▂▂     ▂▂▂             ▂
```

## What each segment shows

| Tool | Source | Display |
|------|--------|---------|
| CC — Claude Code | OAuth usage API (own login via the widget, or the token in `~/.claude/.credentials.json`); falls back to transcript token counts | Exact 5-hour % + weekly % used; extra model-specific windows in the tooltip |
| CX — Codex | Live `https://chatgpt.com/backend-api/wham/usage` (token from `~/.codex/auth.json`, includes usage from all devices); falls back to the newest local session file | Exact 5-hour % + weekly % used |
| AG — Antigravity | `RetrieveUserQuotaSummary` RPC on the local Antigravity language server (CSRF token + port discovered from the running process) | Gemini group 5-hour % + weekly % used; Claude/GPT group in the tooltip. Falls back to activity when Antigravity isn't running |

Each tool shows two mini bars — **5h** (short window) and **wk** (weekly) — with
percentages. Colors: green &lt; 50%, amber &lt; 80%, red ≥ 80%. A `~` prefix means
estimated data. When no percentage data exists, a status text is shown instead
(e.g. CC token estimate, AG session activity). Hover a segment for details
(reset times, plan, hints).

## Claude login

If CC shows a token estimate instead of percentages, the widget can't read a
valid token from `~/.claude/.credentials.json`. Right-click the bar →
**Claude login…** — a browser opens at claude.ai; sign in, click Authorize,
and paste the code back into the dialog. Tokens are stored in the widget's own
file (`%APPDATA%\AIUsageMonitor\claude_oauth.json`) and auto-refresh; Claude
Code's own credentials are never modified.

## Controls

- **Drag** the bar horizontally to reposition it (saved to config).
- **Right-click** → Refresh now / Claude login… / Show Claude Code / Show Codex /
  Show Antigravity (toggle each segment) / Start with Windows / Exit.
- A tray icon ("AI") is also available with refresh/exit.

## Build & run

Open `AIUsageMonitor.sln` in Visual Studio, or from the repo root:

```powershell
dotnet build -c Debug          # dev build
dotnet publish AIUsageMonitor\AIUsageMonitor.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
# → AIUsageMonitor\bin\Release\net8.0-windows\win-x64\publish\AIUsageMonitor.exe
```

Requires .NET 8 runtime (already present with the SDK).

## How the taskbar placement works

Windows 11 removed the DeskBand API, so the widget is a borderless, topmost,
non-activating tool window positioned over the taskbar's empty area, anchored
to the left edge of the tray area (`TrayNotifyWnd`). It re-checks the taskbar
position every 2 seconds, so it follows taskbar/DPI changes. Data refreshes
every 60 seconds (configurable in `%APPDATA%\AIUsageMonitor\config.json`).

Known limitations:
- The bar floats *over* the taskbar; a fullscreen app will cover it (and the
  taskbar) as normal.
- Auto-hide taskbar isn't followed yet.
- Antigravity quota percentages aren't available locally; only activity is shown.

## Files

Standard VS layout: `AIUsageMonitor.sln` at the root, sources in `AIUsageMonitor\`.

- `MainWindow.xaml(.cs)` — the bar UI, positioning, drag, context menu
- `TaskbarInterop.cs` — Win32 taskbar lookup + topmost placement
- `Collectors/` — one collector per tool (`ClaudeCollector`, `CodexCollector`, `AntigravityCollector`)
- `Config.cs` — `%APPDATA%\AIUsageMonitor\config.json` + autostart registry value
- `App.xaml.cs` — single-instance guard + tray icon

Debug log for the Claude API path: `%APPDATA%\AIUsageMonitor\claude_api_debug.log`.
