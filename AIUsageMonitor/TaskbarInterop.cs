using System.Runtime.InteropServices;

namespace AIUsageMonitor;

/// <summary>Win32 helpers to find the taskbar and pin a window onto it.</summary>
public static class TaskbarInterop
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);
    [DllImport("user32.dll")] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr childAfter, string className, string? windowName);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hWnd);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOSIZE = 0x0001;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    public sealed record TaskbarInfo(RECT Bar, int TrayLeft);

    /// <summary>Rect of the primary taskbar and the left edge of its tray-icon area, in device pixels.</summary>
    public static TaskbarInfo? GetTaskbar()
    {
        var tray = FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero || !GetWindowRect(tray, out var barRect)) return null;

        // TrayNotifyWnd = clock + tray icons area on the right end of the bar.
        int trayLeft = barRect.Right;
        var notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify != IntPtr.Zero && GetWindowRect(notify, out var notifyRect) && notifyRect.Left > barRect.Left)
            trayLeft = notifyRect.Left;

        return new TaskbarInfo(barRect, trayLeft);
    }

    /// <summary>Taskbar by index: 0 = primary, 1+ = secondary taskbars (left-to-right). Falls back to primary.</summary>
    public static TaskbarInfo? GetTaskbar(int index)
    {
        if (index > 0)
        {
            var secondaries = GetSecondaryTaskbars();
            if (index - 1 < secondaries.Count)
            {
                var r = secondaries[index - 1];
                // Secondary taskbars have no TrayNotifyWnd — anchor at the right
                // edge; the user can drag left if their setup shows a clock there.
                return new TaskbarInfo(r, r.Right - 8);
            }
        }
        return GetTaskbar();
    }

    /// <summary>Rects of all secondary-monitor taskbars, ordered left-to-right.</summary>
    public static List<RECT> GetSecondaryTaskbars()
    {
        var list = new List<RECT>();
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            if (GetWindowRect(h, out var r)) list.Add(r);
        }
        return list.OrderBy(r => r.Left).ToList();
    }

    /// <summary>Human label ("Monitor 2") for the screen a taskbar rect sits on.</summary>
    public static string ScreenLabelFor(RECT r)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        int cx = (r.Left + r.Right) / 2, cy = (r.Top + r.Bottom) / 2;
        for (int i = 0; i < screens.Length; i++)
        {
            if (screens[i].Bounds.Contains(cx, cy))
                return $"Monitor {i + 1}" + (screens[i].Primary ? " (primary)" : "");
        }
        return "Monitor ?";
    }

    /// <summary>Keep the window out of Alt-Tab and stop it stealing focus when clicked.</summary>
    public static void MakeUnfocusableToolWindow(IntPtr hwnd)
    {
        var ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
    }

    public static void MoveTopMost(IntPtr hwnd, int x, int y)
        => SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE);
}
