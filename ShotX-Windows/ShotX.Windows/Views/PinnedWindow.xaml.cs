using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using ShotX.Windows.Core;

namespace ShotX.Windows.Views;

public partial class PinnedWindow : Window
{
    private readonly Bitmap _bitmap;
    private double _scale = 1.0;

    public PinnedWindow(Bitmap bitmap, System.Windows.Point screenPosition)
    {
        InitializeComponent();
        _bitmap = bitmap;
        ImgPinned.Source = ScreenCapture.ToBitmapSource(bitmap);
        ImgPinned.Width = bitmap.Width;
        ImgPinned.Height = bitmap.Height;

        Left = screenPosition.X - 10;
        Top = screenPosition.Y - 10;
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Window_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        Close();
    }

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta > 0)
        {
            _scale = Math.Min(3.0, _scale + 0.1);
        }
        else
        {
            _scale = Math.Max(0.2, _scale - 0.1);
        }

        ImgPinned.Width = _bitmap.Width * _scale;
        ImgPinned.Height = _bitmap.Height * _scale;
    }

    private void MenuCopy_Click(object sender, RoutedEventArgs e)
    {
        System.Windows.Clipboard.SetImage(ScreenCapture.ToBitmapSource(_bitmap));
    }

    private void MenuSave_Click(object sender, RoutedEventArgs e)
    {
        using var sfd = new SaveFileDialog
        {
            Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|Bitmap Image (*.bmp)|*.bmp",
            FileName = $"ShotX_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            _bitmap.Save(sfd.FileName, ImageFormat.Png);
        }
    }

    private void MenuClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _bitmap.Dispose();
    }
}

