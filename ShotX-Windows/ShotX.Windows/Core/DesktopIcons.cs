using System;
using System.Runtime.InteropServices;

namespace ShotX.Windows.Core;

public static class DesktopIcons
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr FindWindowEx(IntPtr hwndParent, IntPtr hwndChildAfter, string lpszClass, string? lpszWindow);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const int SW_HIDE = 0;
    private const int SW_SHOW = 5;

    private static IntPtr GetDesktopListView()
    {
        IntPtr progman = FindWindow("Progman", null);
        IntPtr shellView = FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (shellView == IntPtr.Zero)
        {
            IntPtr workerW = IntPtr.Zero;
            do
            {
                workerW = FindWindowEx(IntPtr.Zero, workerW, "WorkerW", null);
                shellView = FindWindowEx(workerW, IntPtr.Zero, "SHELLDLL_DefView", null);
            } while (workerW != IntPtr.Zero && shellView == IntPtr.Zero);
        }
        return FindWindowEx(shellView, IntPtr.Zero, "SysListView32", null);
    }

    public static bool IsVisible()
    {
        var hListView = GetDesktopListView();
        return hListView != IntPtr.Zero && IsWindowVisible(hListView);
    }

    public static void Toggle()
    {
        var hListView = GetDesktopListView();
        if (hListView != IntPtr.Zero)
        {
            bool visible = IsWindowVisible(hListView);
            ShowWindow(hListView, visible ? SW_HIDE : SW_SHOW);
        }
    }
}
