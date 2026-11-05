using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Point = System.Windows.Point;

namespace ShotX.Windows.Core;

public class DetectedWindowInfo
{
    public IntPtr Hwnd { get; set; }
    public Rect DipRect { get; set; }
    public string Title { get; set; } = string.Empty;
}

public static class WindowDetectionHelper
{
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out RECT pvAttribute, int cbAttribute);

    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    /// <summary>
    /// 在打开遮罩层前，枚举当前显示器上所有可见、未最小化、非隐藏的顶层窗口（按 Z-Order 从前到后排序）
    /// </summary>
    public static List<DetectedWindowInfo> CaptureVisibleWindows(Rectangle screenBounds, double dpiScaleX, double dpiScaleY)
    {
        var list = new List<DetectedWindowInfo>();
        uint currentPid = (uint)Environment.ProcessId;

        try
        {
            EnumWindows((hWnd, _) =>
            {
                try
                {
                    if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                        return true;

                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (pid == currentPid)
                        return true;

                    // 排除 Windows 10/11 后台挂起或虚拟桌面隐藏的 UWP 窗口
                    if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                        return true;

                    // 排除桌面背景与系统外壳底层容器
                    var clsBuilder = new StringBuilder(128);
                    GetClassName(hWnd, clsBuilder, clsBuilder.Capacity);
                    string className = clsBuilder.ToString();
                    if (className == "Progman" || className == "WorkerW" || className == "Windows.UI.Core.CoreWindow")
                        return true;

                    // 优先获取 DWM 真实视觉边界（去除 Win10/11 透明阴影边框）
                    if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
                    {
                        if (!GetWindowRect(hWnd, out r))
                            return true;
                    }

                    int physW = r.Right - r.Left;
                    int physH = r.Bottom - r.Top;
                    if (physW < 40 || physH < 40)
                        return true;

                    // 计算在当前显示器中的交集区域
                    var winPhysRect = new Rectangle(r.Left, r.Top, physW, physH);
                    var intersect = Rectangle.Intersect(screenBounds, winPhysRect);
                    if (intersect.Width < 30 || intersect.Height < 30)
                        return true;

                    double leftDip = (intersect.Left - screenBounds.Left) / dpiScaleX;
                    double topDip = (intersect.Top - screenBounds.Top) / dpiScaleY;
                    double wDip = intersect.Width / dpiScaleX;
                    double hDip = intersect.Height / dpiScaleY;

                    var titleBuilder = new StringBuilder(256);
                    GetWindowText(hWnd, titleBuilder, titleBuilder.Capacity);
                    string title = titleBuilder.ToString().Trim();

                    list.Add(new DetectedWindowInfo
                    {
                        Hwnd = hWnd,
                        DipRect = new Rect(leftDip, topDip, wDip, hDip),
                        Title = string.IsNullOrEmpty(title) ? className : title
                    });
                }
                catch
                {
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
        }

        return list;
    }

    public static DetectedWindowInfo? FindWindowAtPoint(List<DetectedWindowInfo> windows, Point dipPoint)
    {
        foreach (var w in windows)
        {
            if (w.DipRect.Contains(dipPoint))
            {
                return w;
            }
        }
        return null;
    }
}
