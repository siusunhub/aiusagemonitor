using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using AIUsageMonitor.Collectors;

namespace AIUsageMonitor;

public partial class MainWindow : Window
{
    private readonly Config _config = Config.Load();
    private readonly ToolVm _claude = new("CC");
    private readonly ToolVm _codex = new("CX");
    private readonly ToolVm _antigravity = new("AG");

    private IntPtr _hwnd;
    private bool _dragging;
    private double _dragStartX;
    private int _dragStartOffset;
    private bool _refreshing;
    private bool _menuOpen;

    public MainWindow()
    {
        InitializeComponent();
        ToolVm.YellowAt = _config.YellowAtPercent;
        ToolVm.RedAt = _config.RedAtPercent;
        ToolVm.ShowRemaining = _config.ShowRemaining;
        ToolsList.ItemsSource = new[] { _claude, _codex, _antigravity };
        _claude.SetVisible(_config.ShowClaude);
        _codex.SetVisible(_config.ShowCodex);
        _antigravity.SetVisible(_config.ShowAntigravity);
        UpdateSeparators();
        BuildContextMenu();

        MouseLeftButtonDown += OnDragStart;
        MouseMove += OnDragMove;
        MouseLeftButtonUp += OnDragEnd;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        TaskbarInterop.MakeUnfocusableToolWindow(_hwnd);

        var positionTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        positionTimer.Tick += (_, _) => Reposition();
        positionTimer.Start();

        var refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(15, _config.RefreshSeconds))
        };
        refreshTimer.Tick += async (_, _) => await RefreshAsync();
        refreshTimer.Start();

        // Keep the ⟳ reset countdown ticking between data refreshes.
        var countdownTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        countdownTimer.Tick += (_, _) =>
        {
            _claude.Tick();
            _codex.Tick();
            _antigravity.Tick();
        };
        countdownTimer.Start();

        Reposition();
        if (!_config.BarVisible) Hide();
        _ = RefreshAsync();
    }

    public async Task RefreshAsync()
    {
        if (_refreshing) return;
        _refreshing = true;
        try
        {
            var claudeTask = ClaudeCollector.CollectAsync();
            var codexTask = CodexCollector.CollectAsync();
            var antigravityTask = AntigravityCollector.CollectAsync();
            await Task.WhenAll(claudeTask, codexTask, antigravityTask);

            _claude.Update(claudeTask.Result);
            _codex.Update(codexTask.Result);
            _antigravity.Update(antigravityTask.Result);
        }
        finally
        {
            _refreshing = false;
            Reposition(); // width may have changed with new text
        }
    }

    private void Reposition()
    {
        if (_hwnd == IntPtr.Zero) return;
        // Re-asserting topmost while the context menu is open would push the
        // bar above its own menu — skip until the menu closes.
        if (_menuOpen) return;
        var tb = TaskbarInterop.GetTaskbar(_config.MonitorIndex);
        if (tb == null) return;

        double scale = TaskbarInterop.GetDpiForWindow(_hwnd) / 96.0;
        int widthPx = (int)Math.Round(ActualWidth * scale);
        int heightPx = (int)Math.Round(ActualHeight * scale);

        // Never let the bar leave the monitor on the left.
        int maxOffset = Math.Max(0, tb.TrayLeft - widthPx - (tb.Bar.Left + 4));
        if (_config.OffsetX > maxOffset) _config.OffsetX = maxOffset;

        int barHeight = tb.Bar.Bottom - tb.Bar.Top;
        int x = tb.TrayLeft - widthPx - _config.OffsetX;
        int y = tb.Bar.Top + (barHeight - heightPx) / 2;

        TaskbarInterop.MoveTopMost(_hwnd, x, y);
    }

    // ---- visibility & monitor selection (also used by the tray menu) --------

    public void SetBarVisible(bool visible)
    {
        _config.BarVisible = visible;
        _config.Save();
        if (visible)
        {
            Show();
            Reposition();
        }
        else
        {
            Hide();
        }
    }

    public int CurrentMonitorIndex => _config.MonitorIndex;

    public void SetMonitorIndex(int index)
    {
        _config.MonitorIndex = index;
        _config.Save();
        Reposition();
    }

    // ---- horizontal drag to adjust position ------------------------------

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        // Double-click opens the Claude login dialog when sign-in is needed.
        if (e.ClickCount == 2 && ClaudeCollector.NeedsLogin)
        {
            var dlg = new LoginWindow();
            if (dlg.ShowDialog() == true) _ = RefreshAsync();
            return;
        }

        _dragging = true;
        _dragStartX = PointToScreen(e.GetPosition(this)).X;
        _dragStartOffset = _config.OffsetX;
        CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        double dx = PointToScreen(e.GetPosition(this)).X - _dragStartX;
        _config.OffsetX = Math.Max(0, _dragStartOffset - (int)dx);
        Reposition();
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        ReleaseMouseCapture();
        _config.Save();
    }

    // ---- context menu -----------------------------------------------------

    private void BuildContextMenu()
    {
        var menu = new ContextMenu();

        var refresh = new MenuItem { Header = "Refresh Now" };
        refresh.Click += async (_, _) => await RefreshAsync();
        menu.Items.Add(refresh);

        var login = new MenuItem { Header = "Claude Login…" };
        login.Click += async (_, _) =>
        {
            var dlg = new LoginWindow();
            if (dlg.ShowDialog() == true) await RefreshAsync();
        };
        menu.Items.Add(login);

        var codexMenu = new MenuItem { Header = "Codex Account..." };
        codexMenu.Items.Add(new MenuItem { Header = "…" }); // placeholder so the submenu arrow shows
        codexMenu.SubmenuOpened += (_, _) => RebuildCodexMenu(codexMenu);
        menu.Items.Add(codexMenu);

        menu.Items.Add(new Separator());

        AddShowToggle(menu, "Show Claude Code", _config.ShowClaude,
            v => { _config.ShowClaude = v; _claude.SetVisible(v); });
        AddShowToggle(menu, "Show Codex", _config.ShowCodex,
            v => { _config.ShowCodex = v; _codex.SetVisible(v); });
        AddShowToggle(menu, "Show Antigravity", _config.ShowAntigravity,
            v => { _config.ShowAntigravity = v; _antigravity.SetVisible(v); });

        var showRemaining = new MenuItem
        {
            Header = "Show Remaining",
            IsCheckable = true,
            IsChecked = _config.ShowRemaining,
        };
        showRemaining.Click += (_, _) =>
        {
            _config.ShowRemaining = showRemaining.IsChecked;
            _config.Save();
            ToolVm.ShowRemaining = _config.ShowRemaining;
            // re-render the current data in the new mode
            _claude.Tick();
            _codex.Tick();
            _antigravity.Tick();
        };
        menu.Items.Add(showRemaining);

        menu.Items.Add(new Separator());

        var monitorMenu = new MenuItem { Header = "Show on Monitor" };
        monitorMenu.Items.Add(new MenuItem { Header = "…" }); // placeholder so the submenu arrow shows
        monitorMenu.SubmenuOpened += (_, _) => RebuildMonitorMenu(monitorMenu);
        menu.Items.Add(monitorMenu);

        var hideBar = new MenuItem { Header = "Hide Bar" };
        hideBar.Click += (_, _) => SetBarVisible(false);
        menu.Items.Add(hideBar);

        var autostart = new MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = Config.IsAutostartEnabled(),
        };
        autostart.Click += (_, _) => Config.SetAutostart(autostart.IsChecked);
        menu.Items.Add(autostart);

        var exit = new MenuItem { Header = "Exit" };
        exit.Click += (_, _) => Application.Current.Shutdown();
        menu.Items.Add(exit);

        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => _menuOpen = false;

        ContextMenu = menu;
    }

    private void AddShowToggle(ContextMenu menu, string header, bool initial, Action<bool> apply)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = initial };
        item.Click += (_, _) =>
        {
            apply(item.IsChecked);
            UpdateSeparators();
            _config.Save();
            // let layout settle before re-anchoring to the tray
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
        };
        menu.Items.Add(item);
    }

    /// <summary>Rebuild the "Show on monitor" submenu with the current taskbar list.</summary>
    private void RebuildMonitorMenu(MenuItem parent)
    {
        parent.Items.Clear();

        void AddItem(int index, string label)
        {
            var item = new MenuItem
            {
                Header = label,
                IsCheckable = true,
                IsChecked = _config.MonitorIndex == index,
            };
            item.Click += (_, _) => SetMonitorIndex(index);
            parent.Items.Add(item);
        }

        var primary = TaskbarInterop.GetTaskbar();
        AddItem(0, primary != null ? TaskbarInterop.ScreenLabelFor(primary.Bar) : "Primary taskbar");

        var secondaries = TaskbarInterop.GetSecondaryTaskbars();
        for (int i = 0; i < secondaries.Count; i++)
            AddItem(i + 1, TaskbarInterop.ScreenLabelFor(secondaries[i]));

        if (secondaries.Count == 0)
            parent.Items.Add(new MenuItem { Header = "(no other taskbar found)", IsEnabled = false });
    }

    /// <summary>Rebuild the "Codex account" submenu with the current account list.</summary>
    private void RebuildCodexMenu(MenuItem parent)
    {
        parent.Items.Clear();
        try
        {
            foreach (var acc in CodexAccounts.List())
            {
                var item = new MenuItem
                {
                    Header = CodexAccounts.DisplayName(acc),
                    IsCheckable = true,
                    IsChecked = acc.IsActive,
                };
                var captured = acc;
                item.Click += async (_, _) =>
                {
                    try
                    {
                        CodexAccounts.Switch(captured);
                        await RefreshAsync();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(ex.Message, "Codex account switch");
                    }
                };
                parent.Items.Add(item);
            }
        }
        catch (Exception ex)
        {
            parent.Items.Add(new MenuItem { Header = "error: " + ex.Message, IsEnabled = false });
        }

        parent.Items.Add(new Separator());
        var configure = new MenuItem { Header = "Accounts Setup" };
        configure.Click += async (_, _) =>
        {
            new CodexAccountsWindow().ShowDialog();
            await RefreshAsync();
        };
        parent.Items.Add(configure);
    }

    /// <summary>Show a "|" before every visible segment except the first.</summary>
    private void UpdateSeparators()
    {
        bool anyBefore = false;
        foreach (var vm in new[] { _claude, _codex, _antigravity })
        {
            vm.SetSeparator(anyBefore && vm.IsShown);
            if (vm.IsShown) anyBefore = true;
        }
    }
}
