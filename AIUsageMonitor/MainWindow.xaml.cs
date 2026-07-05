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

    public MainWindow()
    {
        InitializeComponent();
        ToolVm.YellowAt = _config.YellowAtPercent;
        ToolVm.RedAt = _config.RedAtPercent;
        ToolsList.ItemsSource = new[] { _claude, _codex, _antigravity };
        _claude.SetVisible(_config.ShowClaude);
        _codex.SetVisible(_config.ShowCodex);
        _antigravity.SetVisible(_config.ShowAntigravity);
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
        var tb = TaskbarInterop.GetTaskbar();
        if (tb == null) return;

        double scale = TaskbarInterop.GetDpiForWindow(_hwnd) / 96.0;
        int widthPx = (int)Math.Round(ActualWidth * scale);
        int heightPx = (int)Math.Round(ActualHeight * scale);

        int barHeight = tb.Bar.Bottom - tb.Bar.Top;
        int x = tb.TrayLeft - widthPx - _config.OffsetX;
        int y = tb.Bar.Top + (barHeight - heightPx) / 2;

        TaskbarInterop.MoveTopMost(_hwnd, x, y);
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

        var refresh = new MenuItem { Header = "Refresh now" };
        refresh.Click += async (_, _) => await RefreshAsync();
        menu.Items.Add(refresh);

        var login = new MenuItem { Header = "Claude login…" };
        login.Click += async (_, _) =>
        {
            var dlg = new LoginWindow();
            if (dlg.ShowDialog() == true) await RefreshAsync();
        };
        menu.Items.Add(login);

        menu.Items.Add(new Separator());

        AddShowToggle(menu, "Show Claude Code", _config.ShowClaude,
            v => { _config.ShowClaude = v; _claude.SetVisible(v); });
        AddShowToggle(menu, "Show Codex", _config.ShowCodex,
            v => { _config.ShowCodex = v; _codex.SetVisible(v); });
        AddShowToggle(menu, "Show Antigravity", _config.ShowAntigravity,
            v => { _config.ShowAntigravity = v; _antigravity.SetVisible(v); });

        menu.Items.Add(new Separator());

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

        ContextMenu = menu;
    }

    private void AddShowToggle(ContextMenu menu, string header, bool initial, Action<bool> apply)
    {
        var item = new MenuItem { Header = header, IsCheckable = true, IsChecked = initial };
        item.Click += (_, _) =>
        {
            apply(item.IsChecked);
            _config.Save();
            // let layout settle before re-anchoring to the tray
            Dispatcher.BeginInvoke(Reposition, DispatcherPriority.Loaded);
        };
        menu.Items.Add(item);
    }
}
