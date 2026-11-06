using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;
using Windows.Storage.Streams;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using Image = System.Windows.Controls.Image;
using Path = System.Windows.Shapes.Path;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace ShotX.Windows.Views;

/// <summary>
/// 参照 macOS 端 WebcamWindow.swift 实现的左下角圆形摄像头悬浮预览窗。
/// 支持拖拽移动、右下角手柄缩放直径，录制开始后自动锁定并嵌入录屏画面。
/// </summary>
public class WebcamOverlayWindow : Window
{
    private const double MinDiameter = 100;
    private const double MaxDiameter = 360;
    private const double InitialMargin = 18;

    private readonly Grid _rootGrid;
    private readonly Border _circleBorder;
    private readonly Image _cameraImage;
    private readonly Canvas _fallbackCanvas;
    private readonly Border _resizeHandle;
    private readonly DispatcherTimer _captureTimer;

    private MediaCapture? _mediaCapture;
    private bool _cameraInitialized = false;
    private bool _isCapturingFrame = false;
    private bool _isInteractive = true;
    private bool _isResizing = false;
    private Point _resizeStartScreenPt;
    private double _resizeStartDiameter;

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    public WebcamOverlayWindow(Rect selectionDipRect, double monitorDipLeft, double monitorDipTop)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;

        double suggested = Math.Min(selectionDipRect.Width, selectionDipRect.Height) * 0.22;
        double diameter = Math.Max(MinDiameter, Math.Min(MaxDiameter, suggested));

        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = diameter;
        Height = diameter;
        Left = monitorDipLeft + selectionDipRect.Left + InitialMargin;
        Top = Math.Max(monitorDipTop + selectionDipRect.Top, monitorDipTop + selectionDipRect.Bottom - diameter - InitialMargin);

        _rootGrid = new Grid
        {
            Width = diameter,
            Height = diameter,
            Background = Brushes.Transparent
        };

        _circleBorder = new Border
        {
            Width = diameter,
            Height = diameter,
            CornerRadius = new CornerRadius(diameter / 2.0),
            BorderBrush = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255)),
            BorderThickness = new Thickness(3),
            Background = new SolidColorBrush(Color.FromRgb(24, 28, 36)),
            ClipToBounds = true
        };

        var innerGrid = new Grid();

        // 无物理摄像头或初始化中时的质感镜头剪影预览
        _fallbackCanvas = CreateFallbackAvatarView(diameter);
        innerGrid.Children.Add(_fallbackCanvas);

        _cameraImage = new Image
        {
            Stretch = Stretch.UniformToFill,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(-1, 1) // 镜像前置镜头
        };
        innerGrid.Children.Add(_cameraImage);

        _circleBorder.Child = innerGrid;
        _rootGrid.Children.Add(_circleBorder);

        // 右下角缩放手柄 (与 macOS ResizeHandleView 一致)
        _resizeHandle = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = new SolidColorBrush(Color.FromArgb(190, 0, 122, 255)),
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(1.5),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 4, 4),
            Cursor = Cursors.SizeNWSE,
            ToolTip = "拖动调整摄像头画面大小"
        };
        _resizeHandle.Child = new TextBlock
        {
            Text = "⤡",
            Foreground = Brushes.White,
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        _resizeHandle.MouseLeftButtonDown += ResizeHandle_MouseLeftButtonDown;
        _resizeHandle.MouseMove += ResizeHandle_MouseMove;
        _resizeHandle.MouseLeftButtonUp += ResizeHandle_MouseLeftButtonUp;

        _rootGrid.Children.Add(_resizeHandle);
        Content = _rootGrid;

        MouseLeftButtonDown += Window_MouseLeftButtonDown;
        SizeChanged += (_, _) => UpdateCircularClip(Width);

        _captureTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(66) // ~15 FPS 实时镜头预览
        };
        _captureTimer.Tick += async (_, _) => await CaptureCameraFrameAsync();

        Loaded += async (_, _) =>
        {
            UpdateCircularClip(Width);
            await InitCameraAsync();
            _captureTimer.Start();
        };

        Closed += (_, _) =>
        {
            _captureTimer.Stop();
            try { _mediaCapture?.Dispose(); } catch { }
            _mediaCapture = null;
        };
    }

    public void UpdateSelectionPosition(Rect selectionDipRect, double monitorDipLeft, double monitorDipTop)
    {
        if (!_isInteractive) return;
        Left = monitorDipLeft + selectionDipRect.Left + InitialMargin;
        Top = Math.Max(monitorDipTop + selectionDipRect.Top, monitorDipTop + selectionDipRect.Bottom - Height - InitialMargin);
    }

    public void SetInteractive(bool interactive)
    {
        _isInteractive = interactive;
        _resizeHandle.Visibility = interactive ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateCircularClip(double diameter)
    {
        _rootGrid.Width = diameter;
        _rootGrid.Height = diameter;
        _circleBorder.Width = diameter;
        _circleBorder.Height = diameter;
        _circleBorder.CornerRadius = new CornerRadius(diameter / 2.0);
        if (_circleBorder.Child is UIElement child)
        {
            double innerR = Math.Max(10, (diameter - 6) / 2.0);
            child.Clip = new EllipseGeometry(new Point(innerR, innerR), innerR, innerR);
        }
    }

    private static Canvas CreateFallbackAvatarView(double diameter)
    {
        var canvas = new Canvas
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = new LinearGradientBrush(Color.FromRgb(30, 41, 59), Color.FromRgb(15, 23, 42), 45)
        };

        var stack = new StackPanel
        {
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        var icon = new Path
        {
            Data = Geometry.Parse("M 12,4 A 4,4 0 1 1 11.99,4 Z M 4,18 C 4,14 8,12 12,12 C 16,12 20,14 20,18 Z"),
            Fill = new SolidColorBrush(Color.FromArgb(180, 148, 163, 184)),
            Stretch = Stretch.Uniform,
            Width = 42,
            Height = 42,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = "摄像头预览",
            Foreground = new SolidColorBrush(Color.FromArgb(200, 226, 232, 240)),
            FontSize = 10.5,
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new Thickness(0, 6, 0, 0)
        };
        stack.Children.Add(icon);
        stack.Children.Add(label);

        var host = new Border
        {
            Width = diameter,
            Height = diameter,
            Child = stack
        };
        canvas.Children.Add(host);
        return canvas;
    }

    private async Task InitCameraAsync()
    {
        try
        {
            var mc = new MediaCapture();
            var settings = new MediaCaptureInitializationSettings
            {
                StreamingCaptureMode = StreamingCaptureMode.Video
            };
            await mc.InitializeAsync(settings);
            _mediaCapture = mc;
            _cameraInitialized = true;
        }
        catch
        {
            _cameraInitialized = false;
        }
    }

    private async Task CaptureCameraFrameAsync()
    {
        if (!_cameraInitialized || _mediaCapture == null || _isCapturingFrame) return;
        _isCapturingFrame = true;
        try
        {
            using var ras = new InMemoryRandomAccessStream();
            await _mediaCapture.CapturePhotoToStreamAsync(ImageEncodingProperties.CreateJpeg(), ras);
            ras.Seek(0);

            var bytes = new byte[ras.Size];
            using (var dr = new DataReader(ras))
            {
                await dr.LoadAsync((uint)ras.Size);
                dr.ReadBytes(bytes);
            }

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = new MemoryStream(bytes);
            bitmap.EndInit();
            bitmap.Freeze();

            _cameraImage.Source = bitmap;
            _fallbackCanvas.Visibility = Visibility.Collapsed;
        }
        catch
        {
        }
        finally
        {
            _isCapturingFrame = false;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isInteractive || _isResizing) return;
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void ResizeHandle_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_isInteractive) return;
        _isResizing = true;
        if (GetCursorPos(out var pt))
        {
            _resizeStartScreenPt = new Point(pt.X, pt.Y);
        }
        _resizeStartDiameter = Width;
        _resizeHandle.CaptureMouse();
        e.Handled = true;
    }

    private void ResizeHandle_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isResizing) return;
        if (GetCursorPos(out var pt))
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double dx = (pt.X - _resizeStartScreenPt.X) / dpi.DpiScaleX;
            double dy = (pt.Y - _resizeStartScreenPt.Y) / dpi.DpiScaleY;
            double delta = Math.Max(dx, dy);
            double newDiameter = Math.Max(MinDiameter, Math.Min(MaxDiameter, _resizeStartDiameter + delta));
            Width = newDiameter;
            Height = newDiameter;
            UpdateCircularClip(newDiameter);
        }
        e.Handled = true;
    }

    private void ResizeHandle_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isResizing) return;
        _isResizing = false;
        _resizeHandle.ReleaseMouseCapture();
        e.Handled = true;
    }
}

