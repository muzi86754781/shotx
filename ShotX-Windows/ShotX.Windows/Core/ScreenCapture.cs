using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace ShotX.Windows.Core;

public static class ScreenCapture
{
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>
    /// 获取当前鼠标所在的显示器 Screen
    /// </summary>
    public static Screen GetCurrentMouseScreen()
    {
        var cursorPos = System.Windows.Forms.Cursor.Position;
        return Screen.FromPoint(cursorPos) ?? Screen.PrimaryScreen ?? Screen.AllScreens[0];
    }

    /// <summary>
    /// 获取指定屏幕物理点所在显示器的 DPI 缩放比例 (例如 1.0, 1.25, 1.5, 2.0)
    /// </summary>
    public static (double scaleX, double scaleY) GetMonitorDpiScale(Rectangle bounds)
    {
        try
        {
            var pt = new POINT { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
            IntPtr hMon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (hMon != IntPtr.Zero && GetDpiForMonitor(hMon, 0, out uint dpiX, out uint dpiY) == 0 && dpiX > 0 && dpiY > 0)
            {
                return (dpiX / 96.0, dpiY / 96.0);
            }
        }
        catch
        {
        }

        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return (g.DpiX / 96.0, g.DpiY / 96.0);
        }
        catch
        {
            return (1.0, 1.0);
        }
    }

    /// <summary>
    /// 截取指定物理像素矩形区域（例如当前鼠标所在显示器的全屏区域）
    /// </summary>
    public static Bitmap CaptureScreen(Rectangle screenBounds)
    {
        int w = Math.Max(1, screenBounds.Width);
        int h = Math.Max(1, screenBounds.Height);
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            var hdcDest = g.GetHdc();
            var deskHwnd = GetDesktopWindow();
            var hdcSrc = GetWindowDC(deskHwnd);

            BitBlt(hdcDest, 0, 0, w, h, hdcSrc, screenBounds.X, screenBounds.Y, SRCCOPY | CAPTUREBLT);

            ReleaseDC(deskHwnd, hdcSrc);
            g.ReleaseHdc(hdcDest);
        }

        return bmp;
    }

    /// <summary>
    /// 截取全屏（默认截取当前鼠标所在显示器）
    /// </summary>
    public static Bitmap CaptureAllScreens(out Rectangle bounds)
    {
        var screen = GetCurrentMouseScreen();
        bounds = screen.Bounds;
        return CaptureScreen(bounds);
    }

    /// <summary>
    /// 裁剪指定矩形范围的位图
    /// </summary>
    public static Bitmap CropBitmap(Bitmap src, Rectangle rect)
    {
        var targetRect = Rectangle.Intersect(new Rectangle(0, 0, src.Width, src.Height), rect);
        if (targetRect.Width <= 0 || targetRect.Height <= 0)
        {
            return new Bitmap(1, 1);
        }

        var cropped = new Bitmap(targetRect.Width, targetRect.Height, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cropped))
        {
            g.DrawImage(src, new Rectangle(0, 0, targetRect.Width, targetRect.Height), targetRect, GraphicsUnit.Pixel);
        }
        return cropped;
    }

    /// <summary>
    /// 将 GDI+ Bitmap 高性能转换为 WPF 可呈现的 BitmapSource
    /// </summary>
    public static BitmapSource ToBitmapSource(Bitmap bitmap)
    {
        var hBitmap = bitmap.GetHbitmap();
        try
        {
            var source = Imaging.CreateBitmapSourceFromHBitmap(
                hBitmap,
                IntPtr.Zero,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions()
            );
            source.Freeze();
            return source;
        }
        finally
        {
            DeleteObject(hBitmap);
        }
    }
}
