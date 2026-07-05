using System.Drawing;
using System.Windows;

namespace AIUsageMonitor;

public partial class App : Application
{
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
        menu.Items.Add("Exit", null, (_, _) => Shutdown());
        _tray.ContextMenuStrip = menu;
    }

    private static Icon CreateIcon()
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
