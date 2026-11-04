using System;
using System.Windows;
using System.Windows.Input;
using ShotX.Windows.Models;
using Application = System.Windows.Application;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public partial class AllInOneBarWindow : Window
{
    public AllInOneBarWindow()
    {
        InitializeComponent();
        Loaded += AllInOneBarWindow_Loaded;
    }

    private void AllInOneBarWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // 居中于屏幕底部，留 80 像素距离
        var primaryScreen = System.Windows.Forms.Screen.PrimaryScreen ?? System.Windows.Forms.Screen.AllScreens[0];
        var bounds = primaryScreen.Bounds;
        Left = bounds.Left + (bounds.Width - Width) / 2.0;
        Top = bounds.Bottom - Height - 80;
        Activate();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void Window_Deactivated(object? sender, EventArgs e)
    {
        Close();
    }

    private void Area_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Application.Current is App app)
        {
            app.StartScreenshot(OverlayMode.Area);
        }
    }

    private void Fullscreen_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Application.Current is App app)
        {
            app.CaptureFullscreen();
        }
    }

    private void Window_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Application.Current is App app)
        {
            app.StartScreenshot(OverlayMode.Window);
        }
    }

    private void LastArea_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Application.Current is App app)
        {
            app.CapturePreviousArea();
        }
    }

    private void Timer_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Application.Current is App app)
        {
            app.StartTimerCapture(3);
        }
    }

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        Close();
        if (Application.Current is App app)
        {
            app.StartScreenRecording();
        }
    }
}

