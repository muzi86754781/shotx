using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ShotX.Windows.Core;
using ShotX.Windows.Models;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public partial class AllInOneBarWindow : Window
{
    private bool _isClosing = false;

    public AllInOneBarWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Loaded += AllInOneBarWindow_Loaded;
    }

    private void AllInOneBarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 居中于当前鼠标所在显示器的底部，留 80 像素距离
        var screen = ScreenCapture.GetCurrentMouseScreen();
        var bounds = screen.Bounds;
        var (scaleX, scaleY) = ScreenCapture.GetMonitorDpiScale(bounds);

        double screenLeftDip = bounds.Left / scaleX;
        double screenTopDip = bounds.Top / scaleY;
        double screenWidthDip = bounds.Width / scaleX;
        double screenHeightDip = bounds.Height / scaleY;

        Left = screenLeftDip + (screenWidthDip - Width) / 2.0;
        Top = screenTopDip + screenHeightDip - Height - 80;
        Activate();
    }

    private void SafeClose(Action<App>? afterCloseAction = null)
    {
        if (_isClosing) return;
        _isClosing = true;

        Deactivated -= Window_Deactivated;
        try
        {
            Hide();
        }
        catch { }

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Close();
            }
            catch { }

            if (afterCloseAction != null && Application.Current is App app)
            {
                try
                {
                    afterCloseAction(app);
                }
                catch { }
            }
        }), DispatcherPriority.Background);
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SafeClose();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        if (!_isClosing)
        {
            SafeClose();
        }
    }

    private void Area_Click(object sender, RoutedEventArgs e)
    {
        SafeClose(app => app.StartScreenshot(OverlayMode.Area));
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        SafeClose(app => app.CaptureFullscreen());
    }

    private void Window_Click(object sender, RoutedEventArgs e)
    {
        SafeClose(app => app.StartScreenshot(OverlayMode.Window));
    }

    private void LastArea_Click(object sender, RoutedEventArgs e)
    {
        SafeClose(app => app.CapturePreviousArea());
    }

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        SafeClose(app => app.StartTimerCapture(3));
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        SafeClose(app => app.StartScreenRecording());
    }
}
