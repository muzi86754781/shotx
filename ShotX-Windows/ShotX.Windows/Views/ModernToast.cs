using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Application = System.Windows.Application;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;
using Brushes = System.Windows.Media.Brushes;

namespace ShotX.Windows.Views;

/// <summary>
/// 现代化 Fluent 悬浮提示框 (Toast)，完全替代原生系统阻塞弹窗 MessageBox。
/// 具有圆角卡片、平滑淡入淡出动画、无任何系统叮咚提示音、无阻塞自动关闭等特性。
/// </summary>
public class ModernToast : Window
{
    private readonly DispatcherTimer _autoCloseTimer;
    private bool _isClosing = false;

    public ModernToast(string message, string icon = "✓", int durationMs = 2000)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        Opacity = 0;
        Cursor = System.Windows.Input.Cursors.Hand;

        var border = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 15, 23, 42)), // #0F172A 深色半透明
            BorderBrush = new SolidColorBrush(Color.FromArgb(120, 148, 163, 184)), // #94A3B8
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(16, 10, 18, 10),
            Margin = new Thickness(12),
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 4,
                Opacity = 0.38,
                Color = Color.FromRgb(0, 0, 0)
            }
        };

        var sp = new StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };

        // 图标
        var txtIcon = new TextBlock
        {
            Text = icon,
            FontSize = 14,
            FontWeight = FontWeights.Bold,
            Foreground = icon switch
            {
                "✓" => new SolidColorBrush(Color.FromRgb(52, 199, 89)),   // 翠绿
                "⚠️" => new SolidColorBrush(Color.FromRgb(255, 159, 10)), // 琥珀橙
                "✕" => new SolidColorBrush(Color.FromRgb(255, 69, 58)),   // 亮红
                "ℹ️" => new SolidColorBrush(Color.FromRgb(14, 165, 233)), // 天蓝
                _ => new SolidColorBrush(Color.FromRgb(56, 189, 248))
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0)
        };

        // 提示文字
        var txtMessage = new TextBlock
        {
            Text = message,
            FontSize = 12.5,
            FontWeight = FontWeights.Medium,
            Foreground = new SolidColorBrush(Color.FromRgb(248, 250, 252)), // #F8FAFC
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460
        };

        sp.Children.Add(txtIcon);
        sp.Children.Add(txtMessage);
        border.Child = sp;
        Content = border;

        MouseDown += (_, _) => Dismiss();

        _autoCloseTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(durationMs)
        };
        _autoCloseTimer.Tick += (_, _) =>
        {
            _autoCloseTimer.Stop();
            Dismiss();
        };
    }

    public void Display()
    {
        // 定位在当前活动屏幕中下方，优雅展示不挡视线
        var mousePt = System.Windows.Forms.Control.MousePosition;
        var screen = System.Windows.Forms.Screen.FromPoint(mousePt);
        var workArea = screen.WorkingArea;

        Show();

        Left = workArea.Left + (workArea.Width - ActualWidth) / 2;
        Top = workArea.Bottom - ActualHeight - 96;

        var fadeIn = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(160));
        BeginAnimation(OpacityProperty, fadeIn);

        _autoCloseTimer.Start();
    }

    private void Dismiss()
    {
        if (_isClosing) return;
        _isClosing = true;
        _autoCloseTimer.Stop();

        var fadeOut = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(180));
        fadeOut.Completed += (_, _) =>
        {
            Close();
        };
        BeginAnimation(OpacityProperty, fadeOut);
    }

    public static void Show(string message, string icon = "✓", int durationMs = 2200)
    {
        if (Application.Current == null) return;
        if (!Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.BeginInvoke(() => Show(message, icon, durationMs));
            return;
        }

        try
        {
            var toast = new ModernToast(message, icon, durationMs);
            toast.Display();
        }
        catch { }
    }

    public static void ShowSuccess(string message, int durationMs = 2000) => Show(message, "✓", durationMs);
    public static void ShowInfo(string message, int durationMs = 2200) => Show(message, "ℹ️", durationMs);
    public static void ShowWarning(string message, int durationMs = 2500) => Show(message, "⚠️", durationMs);
    public static void ShowError(string message, int durationMs = 2800) => Show(message, "✕", durationMs);
}
