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
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int nWidth, int nHeight);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDesktopWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    private const int SRCCOPY = 0x00CC0020;
    private const int CAPTUREBLT = 0x40000000;

    /// <summary>
    /// 截取全屏（包含所有多显示器联合 VirtualScreen 区域）
    /// </summary>
    public static Bitmap CaptureAllScreens(out Rectangle virtualBounds)
    {
        virtualBounds = SystemInformation.VirtualScreen;
        var bmp = new Bitmap(virtualBounds.Width, virtualBounds.Height, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(bmp))
        {
            var hdcDest = g.GetHdc();
            var hdcSrc = GetWindowDC(GetDesktopWindow());

            BitBlt(hdcDest, 0, 0, virtualBounds.Width, virtualBounds.Height, hdcSrc, virtualBounds.X, virtualBounds.Y, SRCCOPY | CAPTUREBLT);

            ReleaseDC(GetDesktopWindow(), hdcSrc);
            g.ReleaseHdc(hdcDest);
        }

        return bmp;
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
