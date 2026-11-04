using System;
using System.Runtime.InteropServices;
using System.Windows;
using Point = System.Windows.Point;

namespace ShotX.Windows.Core;

public static class WindowDetectionHelper
{
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT p);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    private const uint GA_ROOT = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
        public POINT(int x, int y) { X = x; Y = y; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    public static Rect? GetWindowRectAtPoint(Point screenDipPoint, double dpiScaleX, double dpiScaleY, double screenLeftDip, double screenTopDip)
    {
        try
        {
            int physX = (int)Math.Round((screenDipPoint.X + screenLeftDip) * dpiScaleX);
            int physY = (int)Math.Round((screenDipPoint.Y + screenTopDip) * dpiScaleY);

            var pt = new POINT(physX, physY);
            IntPtr hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return null;

            IntPtr rootHwnd = GetAncestor(hwnd, GA_ROOT);
            if (rootHwnd != IntPtr.Zero) hwnd = rootHwnd;

            if (!IsWindowVisible(hwnd)) return null;

            if (GetWindowRect(hwnd, out RECT r))
            {
                double leftDip = (r.Left / dpiScaleX) - screenLeftDip;
                double topDip = (r.Top / dpiScaleY) - screenTopDip;
                double wDip = (r.Right - r.Left) / dpiScaleX;
                double hDip = (r.Bottom - r.Top) / dpiScaleY;

                if (wDip > 50 && hDip > 50)
                {
                    return new Rect(leftDip, topDip, wDip, hDip);
                }
            }
        }
        catch
        {
        }
        return null;
    }
}
