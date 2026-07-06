using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;

namespace AIUsageMonitor;

public partial class CodexAccountsWindow : Window
{
    private List<CodexAccount> _accounts = new();
    private readonly List<string> _baseTexts = new();
    private int _generation;

    public CodexAccountsWindow()
    {
        InitializeComponent();
        using (var icon = App.CreateIcon())
        {
            Icon = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                icon.Handle, System.Windows.Int32Rect.Empty,
                System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
        }
        Reload();
    }

    private void Reload()
    {
        _generation++;
        _accounts = CodexAccounts.List();
        AccountsList.Items.Clear();
        _baseTexts.Clear();

        foreach (var a in _accounts)
        {
            var text = $"{(a.IsActive ? "●" : "  ")} {CodexAccounts.DisplayName(a)}";
            _baseTexts.Add(text);
            AccountsList.Items.Add(text + "    · loading usage…");
        }

        int gen = _generation;
        for (int i = 0; i < _accounts.Count; i++)
            _ = LoadUsageAsync(i, _accounts[i], gen);
    }

    private async Task LoadUsageAsync(int index, CodexAccount account, int generation)
    {
        var u = await CodexAccounts.FetchUsageAsync(account);
        if (generation != _generation || index >= AccountsList.Items.Count) return;

        // Follow the bar's Used/Remaining display mode, and spell the meaning out.
        string Pct(double? usedPct)
        {
            double p = usedPct ?? 0;
            return ToolVm.ShowRemaining ? $"{100 - p:0}% free" : $"{p:0}% used";
        }

        string suffix = u.Error != null
            ? $"    · {u.Error}"
            : $"    · 5hr: {Pct(u.FiveHourPct)}{Countdown(u.FiveHourReset)} / wk: {Pct(u.WeeklyPct)}{Countdown(u.WeeklyReset)}";
        AccountsList.Items[index] = _baseTexts[index] + suffix;
    }

    private static string Countdown(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } r) return "";
        var left = r - DateTimeOffset.UtcNow;
        if (left <= TimeSpan.Zero) return "";
        return left.TotalDays >= 1 ? $" ({(int)left.TotalDays}d{left.Hours}h)"
             : left.TotalHours >= 1 ? $" ({(int)left.TotalHours}h{left.Minutes:00}m)"
             : $" ({Math.Max(1, left.Minutes)}m)";
    }

    private CodexAccount? Selected =>
        AccountsList.SelectedIndex >= 0 && AccountsList.SelectedIndex < _accounts.Count
            ? _accounts[AccountsList.SelectedIndex] : null;

    private void OnSelectionChanged(object sender, RoutedEventArgs e)
    {
        if (Selected is { } a) AliasBox.Text = CodexAccounts.AliasRegex.IsMatch(a.Alias) ? a.Alias : "";
    }

    // Only English letters and digits can be typed as an alias.
    private void OnAliasInput(object sender, TextCompositionEventArgs e) =>
        e.Handled = !Regex.IsMatch(e.Text, "^[A-Za-z0-9]+$");

    private void OnRename(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } acc) { StatusText.Text = "Select an account first."; return; }
        try
        {
            CodexAccounts.Rename(acc, AliasBox.Text.Trim());
            StatusText.Text = "Renamed.";
            Reload();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var alias = AliasBox.Text.Trim();
        if (!CodexAccounts.IsValidAlias(alias))
        {
            StatusText.Text = "Enter a new alias first (English letters/numbers only).";
            return;
        }

        SetBusy(true);
        StatusText.Text = "Waiting for Codex sign-in — complete the login in the browser…";
        try
        {
            await CodexAccounts.AddAccountAsync(alias);
            StatusText.Text = $"Account \"{alias}\" added and is now active.";
            Reload();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
        finally { SetBusy(false); }
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } acc) { StatusText.Text = "Select an account first."; return; }
        if (acc.IsMaster) { StatusText.Text = "The base account cannot be removed."; return; }

        if (MessageBox.Show(this, $"Remove account \"{acc.Alias}\" ({acc.Email})?",
                "Codex accounts", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        try
        {
            CodexAccounts.Remove(acc);
            StatusText.Text = "Removed.";
            Reload();
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }

    private void SetBusy(bool busy)
    {
        AddBtn.IsEnabled = RenameBtn.IsEnabled = RemoveBtn.IsEnabled = AliasBox.IsEnabled = !busy;
        Cursor = busy ? Cursors.Wait : null;
    }
}
