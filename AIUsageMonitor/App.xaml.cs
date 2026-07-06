using System.Drawing;
using System.Windows;

namespace AIUsageMonitor;

public partial class App : Application
{
    /// <summary>Application version (record only).</summary>
    public const string Version = "v0.1";

    private Mutex? _singleInstance;
    private System.Windows.Forms.NotifyIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "AIUsageMonitor_SingleInstance", out bool isNew);
        if (!isNew)
        {
            Shutdown();
            return;
        }

        base.OnStartup(e);
        SetupTrayIcon();
    }

    private void SetupTrayIcon()
    {
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = CreateIcon(),
            Text = "AI Usage Monitor",
            Visible = true,
        };

        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Refresh now", null, async (_, _) =>
        {
            if (MainWindow is MainWindow w) await w.RefreshAsync();
        });

        var codexMenu = new System.Windows.Forms.ToolStripMenuItem("Codex account");
        codexMenu.DropDownItems.Add("…"); // placeholder so the submenu arrow shows
        codexMenu.DropDownOpening += (_, _) => RebuildTrayCodexMenu(codexMenu);
        menu.Items.Add(codexMenu);

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());

        var showBar = new System.Windows.Forms.ToolStripMenuItem("Show usage bar");
        showBar.Click += (_, _) =>
        {
            if (MainWindow is MainWindow w) w.SetBarVisible(!w.IsVisible);
        };
        menu.Items.Add(showBar);

        var monitorMenu = new System.Windows.Forms.ToolStripMenuItem("Show on monitor");
        monitorMenu.DropDownItems.Add("…"); // placeholder so the submenu arrow shows
        monitorMenu.DropDownOpening += (_, _) => RebuildTrayMonitorMenu(monitorMenu);
        menu.Items.Add(monitorMenu);

        menu.Opening += (_, _) => showBar.Checked = MainWindow?.IsVisible == true;

        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    private void RebuildTrayMonitorMenu(System.Windows.Forms.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        if (MainWindow is not MainWindow w) return;

        void AddItem(int index, string label)
        {
            var item = new System.Windows.Forms.ToolStripMenuItem(label)
            {
                Checked = w.CurrentMonitorIndex == index,
            };
            item.Click += (_, _) => w.SetMonitorIndex(index);
            parent.DropDownItems.Add(item);
        }

        var primary = TaskbarInterop.GetTaskbar();
        AddItem(0, primary != null ? TaskbarInterop.ScreenLabelFor(primary.Bar) : "Primary taskbar");

        var secondaries = TaskbarInterop.GetSecondaryTaskbars();
        for (int i = 0; i < secondaries.Count; i++)
            AddItem(i + 1, TaskbarInterop.ScreenLabelFor(secondaries[i]));

        if (secondaries.Count == 0)
        {
            parent.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("(no other taskbar found)")
            {
                Enabled = false,
            });
        }
    }

    private void RebuildTrayCodexMenu(System.Windows.Forms.ToolStripMenuItem parent)
    {
        parent.DropDownItems.Clear();
        try
        {
            foreach (var acc in CodexAccounts.List())
            {
                var item = new System.Windows.Forms.ToolStripMenuItem(CodexAccounts.DisplayName(acc))
                {
                    Checked = acc.IsActive,
                };
                var captured = acc;
                item.Click += async (_, _) =>
                {
                    try
                    {
                        CodexAccounts.Switch(captured);
                        if (MainWindow is MainWindow w) await w.RefreshAsync();
                    }
                    catch (Exception ex)
                    {
                        System.Windows.Forms.MessageBox.Show(ex.Message, "Codex account switch");
                    }
                };
                parent.DropDownItems.Add(item);
            }
        }
        catch (Exception ex)
        {
            parent.DropDownItems.Add(new System.Windows.Forms.ToolStripMenuItem("error: " + ex.Message)
            {
                Enabled = false,
            });
        }

        parent.DropDownItems.Add(new System.Windows.Forms.ToolStripSeparator());
        parent.DropDownItems.Add("Configure accounts…", null, async (_, _) =>
        {
            new CodexAccountsWindow().ShowDialog();
            if (MainWindow is MainWindow w) await w.RefreshAsync();
        });
    }

    /// <summary>The generated "AI" icon, shared by the tray and dialog windows.</summary>
    internal static Icon CreateIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using (var bg = new SolidBrush(Color.FromArgb(255, 40, 40, 46)))
            g.FillEllipse(bg, 1, 1, 30, 30);
        using var font = new Font("Segoe UI", 12, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
        var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
        g.DrawString("AI", font, Brushes.White, new RectangleF(0, 0, 32, 32), fmt);
        return Icon.FromHandle(bmp.GetHicon());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_tray != null)
        {
            _tray.Visible = false;
            _tray.Dispose();
        }
        base.OnExit(e);
    }
}
