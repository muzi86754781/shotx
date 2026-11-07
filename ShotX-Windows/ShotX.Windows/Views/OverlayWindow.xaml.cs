using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotX.Windows.Core;
using ShotX.Windows.Models;
using Color = System.Windows.Media.Color;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Rect = System.Windows.Rect;
using Cursors = System.Windows.Input.Cursors;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using PixelFormat = System.Drawing.Imaging.PixelFormat;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using Button = System.Windows.Controls.Button;

namespace ShotX.Windows.Views;

public partial class OverlayWindow : Window
{
    private enum CaptureMode
    {
        Selecting,
        Annotating,
        Scrolling
    }

    private enum HitArea
    {
        None,
        Move,
        TopLeft,
        TopRight,
        BottomLeft,
        BottomRight,
        LeftEdge,
        RightEdge,
        TopEdge,
        BottomEdge
    }

    private readonly Bitmap _fullSnapshot;
    private readonly System.Drawing.Rectangle _virtualBounds;
    private CaptureMode _mode = CaptureMode.Selecting;

    private Point? _startPoint;
    private Rect _selectionRect = Rect.Empty;
    private Point _currentMouse;
    private HitArea _activeHitArea = HitArea.None;
    private Point _dragStartMouse;
    private Rect _dragStartSelection;

    // Top Bar State
    private int _cornerRadius = 0;
    private bool _includeShadow = false;
    private bool _isDraggingCornerSlider = false;

    // Annotation State
    private readonly AnnotationManager _annotationManager = new();
    private Annotation? _inProgressAnnotation;
    private Annotation? _editingTextAnnotation;
    private ToolbarTool _currentTool = ToolbarTool.None;
    private int _hoveredButtonIndex = -1;

    // Sub-toolbar State
    private bool _isSubToolbarVisible = false;
    private Color _currentColor = Color.FromRgb(255, 59, 48); // #FF3B30
    private Color _secondaryColor = Colors.White;
    private bool _textSelectingSecondary = false;
    private double _currentThickness = 4.0;
    private double _currentOpacity = 1.0;

    // Tool specific sub-toolbar options
    private bool _isRectFilled = false;
    private bool _isRectRounded = false;
    private bool _isOvalFilled = false;
    private bool _isArrowCurved = false;
    private bool _isHighlighter = false;
    private bool _stepIsFilled = true;
    private int _stepCounter = 1;
    private int _textStyleIndex = 1; // 0 = 无, 1 = 描边, 2 = 背景
    private bool _mosaicIsBlur = false; // false = 马赛克, true = 毛玻璃

    private bool _isDraggingSizeSlider = false;
    private bool _isDraggingOpacitySlider = false;

    // Scrolling screenshot State
    private readonly ScrollStitcher _stitcher = new();
    private Rect? _hoveredWindowRect;
    private string? _hoveredWindowTitle;
    private List<DetectedWindowInfo> _detectedWindows = new();

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    [DllImport("user32.dll")]
    private static extern uint SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);
    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(System.Drawing.Point Point);
    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private bool _isStitchingInProgress = false;

    // 8 Sub-Toolbar Preset Colors
    private static readonly Color[] SubToolbarColors = new[]
    {
        Colors.White,
        Color.FromRgb(30, 30, 30),
        Color.FromRgb(255, 59, 48),  // 红色 #FF3B30
        Color.FromRgb(255, 149, 0),  // 橙色 #FF9500
        Color.FromRgb(255, 204, 0),  // 黄色 #FFCC00
        Color.FromRgb(52, 199, 89),  // 绿色 #34C759
        Color.FromRgb(0, 122, 255),  // 蓝色 #007AFF
        Color.FromRgb(175, 82, 222)  // 紫色 #AF52DE
    };

    // 比例锁定与快捷操作 (图 2 像素级对齐)
    private int _lockedRatioIndex = 0; // 0=Free, 1=1:1, 2=2:3, 3=3:4, 4=4:5, 5=5:7, 6=9:16, 7=16:9, 8=3:2, 9=4:3
    private static readonly double[] AspectRatios = { 0, 1.0, 2.0 / 3.0, 3.0 / 4.0, 4.0 / 5.0, 5.0 / 7.0, 9.0 / 16.0, 16.0 / 9.0, 3.0 / 2.0, 4.0 / 3.0 };
    private static readonly string[] AspectRatioLabels = { "Free", "1:1", "2:3", "3:4", "4:5", "5:7", "9:16", "16:9", "3:2", "4:3" };
    private string? _toastMessage = null;
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.0) };

    private readonly OverlayMode _overlayMode;

    public OverlayWindow(OverlayMode overlayMode = OverlayMode.AllInOne, Rect? initialRect = null)
    {
        InitializeComponent();
        _overlayMode = overlayMode;

        // 仅在当前鼠标所在的显示器屏幕打开，不横跨多个显示器
        var currentScreen = ScreenCapture.GetCurrentMouseScreen();
        _virtualBounds = currentScreen.Bounds;
        var (dpiScaleX, dpiScaleY) = ScreenCapture.GetMonitorDpiScale(_virtualBounds);

        if (_overlayMode == OverlayMode.Window)
        {
            _detectedWindows = WindowDetectionHelper.CaptureVisibleWindows(_virtualBounds, dpiScaleX, dpiScaleY);
        }

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _virtualBounds.Left / dpiScaleX;
        Top = _virtualBounds.Top / dpiScaleY;
        Width = _virtualBounds.Width / dpiScaleX;
        Height = _virtualBounds.Height / dpiScaleY;

        _fullSnapshot = ScreenCapture.CaptureScreen(_virtualBounds);
        ImgSnapshot.Source = ScreenCapture.ToBitmapSource(_fullSnapshot);
        ImgSnapshot.Width = Width;
        ImgSnapshot.Height = Height;

        OverlayCanvas.CustomRender = DrawCanvas;

        _toastTimer.Tick += (_, _) =>
        {
            _toastMessage = null;
            _toastTimer.Stop();
            Render();
        };

        _cornerRadius = PreferencesStore.Current.CornerRadius;
        _includeShadow = PreferencesStore.Current.IncludeShadow;

        if (initialRect.HasValue && initialRect.Value.Width > 5 && initialRect.Value.Height > 5)
        {
            var clamped = Rect.Intersect(initialRect.Value, new Rect(0, 0, Width, Height));
            if (!clamped.IsEmpty && clamped.Width > 5 && clamped.Height > 5)
            {
                _selectionRect = clamped;
                _mode = CaptureMode.Annotating;
            }
        }

        if (_overlayMode == OverlayMode.Window && _detectedWindows.Count > 0)
        {
            var cursorPhys = System.Windows.Forms.Cursor.Position;
            var initMouseDip = new Point(
                (cursorPhys.X - _virtualBounds.Left) / dpiScaleX,
                (cursorPhys.Y - _virtualBounds.Top) / dpiScaleY
            );
            _currentMouse = initMouseDip;
            var hovered = WindowDetectionHelper.FindWindowAtPoint(_detectedWindows, initMouseDip);
            if (hovered != null)
            {
                _hoveredWindowRect = hovered.DipRect;
                _hoveredWindowTitle = hovered.Title;
            }
        }

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, _virtualBounds.Left, _virtualBounds.Top, _virtualBounds.Width, _virtualBounds.Height, SWP_SHOWWINDOW);
            }
            var dpi = VisualTreeHelper.GetDpi(this);
            Width = _virtualBounds.Width / dpi.DpiScaleX;
            Height = _virtualBounds.Height / dpi.DpiScaleY;
            ImgSnapshot.Width = Width;
            ImgSnapshot.Height = Height;
        };

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Render();
        };
    }

    /// <summary>
    /// 改图模式构造函数：加载外部图片或剪贴板图片并直接进入可标注编辑状态 (解决需求 7)
    /// </summary>
    public OverlayWindow(Bitmap editBitmap) : this(OverlayMode.Area, null)
    {
        var (dpiScaleX, dpiScaleY) = ScreenCapture.GetMonitorDpiScale(_virtualBounds);

        double maxPhysW = _virtualBounds.Width * 0.78;
        double maxPhysH = _virtualBounds.Height * 0.75;
        double scale = Math.Min(1.0, Math.Min(maxPhysW / Math.Max(1, editBitmap.Width), maxPhysH / Math.Max(1, editBitmap.Height)));

        int drawPhysW = Math.Max(40, (int)Math.Round(editBitmap.Width * scale));
        int drawPhysH = Math.Max(40, (int)Math.Round(editBitmap.Height * scale));
        int drawPhysX = (_virtualBounds.Width - drawPhysW) / 2;
        int drawPhysY = Math.Max(30, (_virtualBounds.Height - drawPhysH) / 2 - 20);

        using (var g = Graphics.FromImage(_fullSnapshot))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using var bgBrush = new SolidBrush(System.Drawing.Color.FromArgb(255, 255, 255));
            g.FillRectangle(bgBrush, drawPhysX, drawPhysY, drawPhysW, drawPhysH);
            g.DrawImage(editBitmap, new System.Drawing.Rectangle(drawPhysX, drawPhysY, drawPhysW, drawPhysH));
        }

        ImgSnapshot.Source = ScreenCapture.ToBitmapSource(_fullSnapshot);

        _selectionRect = new Rect(
            drawPhysX / dpiScaleX,
            drawPhysY / dpiScaleY,
            drawPhysW / dpiScaleX,
            drawPhysH / dpiScaleY
        );
        _mode = CaptureMode.Annotating;
        _currentTool = ToolbarTool.Rectangle;
        _isSubToolbarVisible = true;
        ShowToast("已进入改图模式：可直接添加标注、调整四角、复制或保存");
    }

    #region Rendering

    private void Render()
    {
        OverlayCanvas.InvalidateVisual();
    }

    private void DrawCanvas(DrawingContext dc)
    {
        // 铺底近透明层确保全屏任何位置均可响应鼠标框选与移动事件
        var fullRect = new Rect(0, 0, Width, Height);
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, fullRect);

        // 1. 绘制半透明灰色遮罩（窗口截图悬停时高亮镂空目标窗口，框选时镂空选区）
        Rect clearRect = !_selectionRect.IsEmpty
            ? _selectionRect
            : (_overlayMode == OverlayMode.Window && _hoveredWindowRect.HasValue ? _hoveredWindowRect.Value : Rect.Empty);

        var maskGeometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(fullRect),
            new RectangleGeometry(clearRect)
        );
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, maskGeometry);

        if (_mode == CaptureMode.Selecting)
        {
            if (!_selectionRect.IsEmpty)
            {
                dc.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1.5), _selectionRect);
                DrawCornerBrackets(dc, _selectionRect);
                DrawTopBar(dc);
            }
            else if (_overlayMode == OverlayMode.Window)
            {
                // 窗口截图模式：高亮跟随鼠标下的窗口，点击即可截图
                if (_hoveredWindowRect.HasValue)
                {
                    var winR = _hoveredWindowRect.Value;
                    dc.DrawRoundedRectangle(
                        new SolidColorBrush(Color.FromArgb(18, 0, 122, 255)),
                        new Pen(new SolidColorBrush(Color.FromRgb(0, 122, 255)), 3),
                        winR, 6, 6
                    );
                    DrawCornerBrackets(dc, winR);

                    string label = string.IsNullOrWhiteSpace(_hoveredWindowTitle)
                        ? $"窗口 ({(int)winR.Width} × {(int)winR.Height}) · 点击即可截图"
                        : $"{_hoveredWindowTitle} ({(int)winR.Width} × {(int)winR.Height}) · 点击即可截图";

                    var ftWin = new FormattedText(
                        label,
                        System.Globalization.CultureInfo.CurrentCulture,
                        FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                        12,
                        Brushes.White,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip
                    );
                    double badgeW = Math.Min(Width - 20, ftWin.Width + 20);
                    double badgeH = ftWin.Height + 10;
                    double badgeX = Math.Max(10, Math.Min(Width - badgeW - 10, winR.Left));
                    double badgeY = winR.Top >= badgeH + 8 ? winR.Top - badgeH - 6 : winR.Top + 8;
                    var badgeRect = new Rect(badgeX, badgeY, badgeW, badgeH);
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, badgeRect, 5, 5);
                    dc.DrawText(ftWin, new Point(badgeRect.Left + 10, badgeRect.Top + 5));
                }
            }
            else
            {
                // 十字准星贯穿当前显示器屏幕 (图 2)
                var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), 1)
                {
                    DashStyle = DashStyles.Dash
                };
                dc.DrawLine(crossPen, new Point(0, _currentMouse.Y), new Point(Width, _currentMouse.Y));
                dc.DrawLine(crossPen, new Point(_currentMouse.X, 0), new Point(_currentMouse.X, Height));

                DrawLoupe(dc, _currentMouse);
                if (_overlayMode != OverlayMode.ColorPicker)
                {
                    DrawQuickHelpCard(dc);
                }
            }
        }
        else if (_mode == CaptureMode.Annotating)
        {
            // 选框与边角
            dc.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1.5), _selectionRect);
            DrawCornerBrackets(dc, _selectionRect);

            // 绘制马赛克/毛玻璃图层
            DrawMosaics(dc);

            // 绘制所有已完成标注
            foreach (var ann in _annotationManager.Items)
            {
                if (ann != _editingTextAnnotation && ann.Type != ShapeType.Mosaic)
                {
                    DrawAnnotation(dc, ann);
                }
            }

            // 绘制正在拖拽绘制中的标注
            if (_inProgressAnnotation != null && _inProgressAnnotation.Type != ShapeType.Mosaic)
            {
                DrawAnnotation(dc, _inProgressAnnotation);
            }

            // 顶部条 (尺寸、圆角滑块、阴影开关)
            DrawTopBar(dc);

            // 底部 16 项胶囊工具栏
            DrawBottomToolbar(dc);

            // 展开的子工具栏
            if (_isSubToolbarVisible && _currentTool != ToolbarTool.None)
            {
                DrawSubToolbar(dc);
            }
        }
        else if (_mode == CaptureMode.Scrolling)
        {
            dc.DrawRectangle(null, new Pen(Brushes.DodgerBlue, 1.5), _selectionRect);
            DrawCornerBrackets(dc, _selectionRect);
            DrawScrollingPreviewPanel(dc);
            DrawBottomToolbar(dc);
        }

        // 浮动 Toast 提示
        if (!string.IsNullOrEmpty(_toastMessage))
        {
            DrawToast(dc, _toastMessage);
        }
    }

    private void DrawCornerBrackets(DrawingContext dc, Rect r)
    {
        var accentBrush = new SolidColorBrush(Color.FromRgb(46, 135, 255));
        var pen = new Pen(accentBrush, 3.5)
        {
            StartLineCap = PenLineCap.Square,
            EndLineCap = PenLineCap.Square
        };
        double len = 16.0;

        // 4 角 L 型边角高亮
        dc.DrawLine(pen, new Point(r.Left - 1, r.Top + len), new Point(r.Left - 1, r.Top - 1));
        dc.DrawLine(pen, new Point(r.Left - 1, r.Top - 1), new Point(r.Left + len, r.Top - 1));

        dc.DrawLine(pen, new Point(r.Right - len, r.Top - 1), new Point(r.Right + 1, r.Top - 1));
        dc.DrawLine(pen, new Point(r.Right + 1, r.Top - 1), new Point(r.Right + 1, r.Top + len));

        dc.DrawLine(pen, new Point(r.Left - 1, r.Bottom - len), new Point(r.Left - 1, r.Bottom + 1));
        dc.DrawLine(pen, new Point(r.Left - 1, r.Bottom + 1), new Point(r.Left + len, r.Bottom + 1));

        dc.DrawLine(pen, new Point(r.Right - len, r.Bottom + 1), new Point(r.Right + 1, r.Bottom + 1));
        dc.DrawLine(pen, new Point(r.Right + 1, r.Bottom + 1), new Point(r.Right + 1, r.Bottom - len));

        // 4 角明显可拖拽锚点圆点，提示用户可随时拖拽四角重新框选调整
        var handlePen = new Pen(accentBrush, 2.0);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Left, r.Top), 5, 5);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Right, r.Top), 5, 5);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Left, r.Bottom), 5, 5);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Right, r.Bottom), 5, 5);
    }

    private Rect TopBarRect()
    {
        double w = 230;
        double h = 28;
        double x = Math.Max(10, Math.Min(Width - w - 10, _selectionRect.Left));
        double y = _selectionRect.Top - h - 6;
        if (y < 8)
        {
            y = _selectionRect.Top + 8;
        }
        return new Rect(x, y, w, h);
    }

    private void DrawTopBar(DrawingContext dc)
    {
        var r = TopBarRect();

        // 蓝色胶囊背景
        var pillBrush = new SolidColorBrush(Color.FromRgb(31, 135, 250));
        dc.DrawRoundedRectangle(pillBrush, null, r, 6, 6);

        // 1. 尺寸文本: "W × H"
        string sizeText = $"{(int)_selectionRect.Width} × {(int)_selectionRect.Height}";
        var ft = new FormattedText(
            sizeText,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface(new System.Windows.Media.FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            11.5,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ft, new Point(r.Left + 8, r.Top + (r.Height - ft.Height) / 2));

        // 2. 圆角滑块 (0 ~ 36 px)
        double sliderMinX = r.Left + 88;
        double sliderW = 55;
        var trackRect = new Rect(sliderMinX, r.Top + 12, sliderW, 4);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), null, trackRect, 2, 2);

        double knobRatio = Math.Max(0, Math.Min(1, _cornerRadius / 36.0));
        double knobX = sliderMinX + knobRatio * sliderW;
        dc.DrawEllipse(Brushes.White, null, new Point(knobX, r.Top + 14), 6, 6);

        // 3. 阴影开关
        var switchRect = new Rect(r.Left + 160, r.Top + 7, 22, 14);
        if (_includeShadow)
        {
            dc.DrawRoundedRectangle(Brushes.White, null, switchRect, 7, 7);
            dc.DrawEllipse(pillBrush, null, new Point(switchRect.Right - 7, switchRect.Top + 7), 5, 5);
        }
        else
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), null, switchRect, 7, 7);
            dc.DrawEllipse(Brushes.White, null, new Point(switchRect.Left + 7, switchRect.Top + 7), 5, 5);
        }

        var shadowFt = new FormattedText(
            "阴影",
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"),
            11,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(shadowFt, new Point(r.Left + 188, r.Top + (r.Height - shadowFt.Height) / 2));
    }

    private int _colorFormatMode = 0; // 0: HEX, 1: RGB, 2: HSL, 3: HEXA (解决需求 2 快捷键切换颜色类型)

    private static string RgbToHslString(byte r, byte g, byte b)
    {
        double rf = r / 255.0;
        double gf = g / 255.0;
        double bf = b / 255.0;
        double max = Math.Max(rf, Math.Max(gf, bf));
        double min = Math.Min(rf, Math.Min(gf, bf));
        double delta = max - min;
        double h = 0.0, s = 0.0, l = (max + min) / 2.0;

        if (delta > 0.00001)
        {
            s = l > 0.5 ? delta / (2.0 - max - min) : delta / (max + min);
            if (max == rf) h = ((gf - bf) / delta) + (gf < bf ? 6.0 : 0.0);
            else if (max == gf) h = ((bf - rf) / delta) + 2.0;
            else h = ((rf - gf) / delta) + 4.0;
            h *= 60.0;
        }
        return $"hsl({(int)Math.Round(h)}, {(int)Math.Round(s * 100)}%, {(int)Math.Round(l * 100)}%)";
    }

    private void DrawLoupe(DrawingContext dc, Point p)
    {
        double loupeSize = 136;
        double x = p.X + 22;
        double y = p.Y + 22;
        if (x + loupeSize > Width) x = p.X - loupeSize - 22;
        if (y + loupeSize + 48 > Height) y = p.Y - loupeSize - 48;

        int px = Math.Max(0, Math.Min(_fullSnapshot.Width - 1, (int)p.X));
        int py = Math.Max(0, Math.Min(_fullSnapshot.Height - 1, (int)p.Y));
        var c = _fullSnapshot.GetPixel(px, py);

        var loupeRect = new Rect(x, y, loupeSize, loupeSize);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(160, 160, 165)), 1.5), loupeRect, 8, 8);

        // 9x9 局部点阵放大网格
        int gridSize = 9;
        double cellSize = loupeSize / gridSize;
        int halfGrid = gridSize / 2;

        for (int gy = 0; gy < gridSize; gy++)
        {
            for (int gx = 0; gx < gridSize; gx++)
            {
                int sampleX = Math.Max(0, Math.Min(_fullSnapshot.Width - 1, px + (gx - halfGrid)));
                int sampleY = Math.Max(0, Math.Min(_fullSnapshot.Height - 1, py + (gy - halfGrid)));
                var sc = _fullSnapshot.GetPixel(sampleX, sampleY);
                var cellRect = new Rect(x + gx * cellSize, y + gy * cellSize, cellSize, cellSize);
                dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(sc.R, sc.G, sc.B)), null, cellRect);
                dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)), 0.5), cellRect);
            }
        }

        // 中心瞄准线红框 (准星)
        var centerCell = new Rect(x + halfGrid * cellSize, y + halfGrid * cellSize, cellSize, cellSize);
        dc.DrawRectangle(null, new Pen(Brushes.Red, 1.5), centerCell);

        // 底部颜色信息标签卡片
        var infoRect = new Rect(x, y + loupeSize + 4, loupeSize, 44);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 25, 27, 32)), null, infoRect, 6, 6);

        // 当前颜色方块
        var colorBox = new Rect(infoRect.Left + 8, infoRect.Top + 8, 14, 14);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), new Pen(Brushes.White, 1), colorBox, 2, 2);

        string formattedValue = _colorFormatMode switch
        {
            1 => $"rgb({c.R}, {c.G}, {c.B})",
            2 => RgbToHslString(c.R, c.G, c.B),
            3 => $"#{c.R:X2}{c.G:X2}{c.B:X2}FF",
            _ => $"#{c.R:X2}{c.G:X2}{c.B:X2}"
        };
        string formatName = _colorFormatMode switch
        {
            1 => "RGB",
            2 => "HSL",
            3 => "HEXA",
            _ => "HEX"
        };

        var ftColor = new FormattedText(
            $"{formatName}: {formattedValue}",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas, Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            10.0,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftColor, new Point(infoRect.Left + 26, infoRect.Top + 7));

        var ftTip = new FormattedText(
            "[Tab/C] 切换格式 · 点击复制",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas, Microsoft YaHei"),
            9.0,
            new SolidColorBrush(Color.FromRgb(180, 185, 195)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftTip, new Point(infoRect.Left + 8, infoRect.Top + 25));
    }

    private void DrawQuickHelpCard(DrawingContext dc)
    {
        double cardW = 390;
        double cardH = 175;
        double cardX = 24;
        double cardY = Height - cardH - 24;
        var bgRect = new Rect(cardX, cardY, cardW, cardH);

        // 深色半透明圆角卡片 (图 2 像素级复刻)
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(225, 22, 24, 28)), new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1), bgRect, 10, 10);

        string[] lines = new string[]
        {
            "单击 D 选择上一次截图区域",
            "单击 F 选择全屏",
            "点按数字按键锁定框选区域宽度:高度比例",
            "0=Free  1=1:1  2=2:3  3=3:4  4=4:5  5=5:7  6=9:16  7=16:9  8=3:2  9=4:3",
            "单击 ESC 或者右键退出截图",
            "单击 R 拷贝 RGB 颜色",
            "单击 H 拷贝 HEX 颜色",
            "单击 Z 拷贝自定义颜色"
        };

        double curY = cardY + 12;
        var typeface = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var boldTypeface = new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        for (int i = 0; i < lines.Length; i++)
        {
            var tf = (i == 2 || i == 3) ? boldTypeface : typeface;
            var color = (i == 3) ? new SolidColorBrush(Color.FromRgb(100, 180, 255)) : new SolidColorBrush(Color.FromArgb(230, 240, 240, 240));
            var ft = new FormattedText(
                lines[i],
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                tf,
                11,
                color,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            dc.DrawText(ft, new Point(cardX + 14, curY));
            curY += 18.5;
        }
    }

    private void DrawToast(DrawingContext dc, string text)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12.5,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );

        double w = ft.Width + 28;
        double h = 34;
        double x = (Width - w) / 2.0;
        double y = 48;

        var r = new Rect(x, y, w, h);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(230, 20, 20, 24)), new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1), r, 8, 8);
        dc.DrawText(ft, new Point(x + (w - ft.Width) / 2, y + (h - ft.Height) / 2));
    }

    private void ShowToast(string text)
    {
        _toastMessage = text;
        _toastTimer.Stop();
        _toastTimer.Start();
        Render();
    }

    private static string GetColorName(System.Drawing.Color c)
    {
        int max = Math.Max(c.R, Math.Max(c.G, c.B));
        int min = Math.Min(c.R, Math.Min(c.G, c.B));
        if (max - min < 22)
        {
            if (max < 40) return "纯黑色";
            if (max < 85) return "深灰色";
            if (max < 160) return "灰色";
            if (max < 225) return "浅灰色";
            return "纯白色";
        }

        float hue = c.GetHue();
        if (hue >= 345 || hue < 15) return "红色";
        if (hue >= 15 && hue < 45) return "橙色";
        if (hue >= 45 && hue < 70) return "黄色";
        if (hue >= 70 && hue < 165) return "绿色";
        if (hue >= 165 && hue < 200) return "青色";
        if (hue >= 200 && hue < 260) return "蓝色";
        if (hue >= 260 && hue < 315) return "紫色";
        return "品红色";
    }

    private void CopyCurrentColor(string format)
    {
        try
        {
            int px = Math.Max(0, Math.Min(_fullSnapshot.Width - 1, (int)_currentMouse.X));
            int py = Math.Max(0, Math.Min(_fullSnapshot.Height - 1, (int)_currentMouse.Y));
            var c = _fullSnapshot.GetPixel(px, py);

            string value = format switch
            {
                "RGB" => $"rgb({c.R}, {c.G}, {c.B})",
                "HEX" => $"#{c.R:X2}{c.G:X2}{c.B:X2}",
                "NAME" => $"{GetColorName(c)} #{c.R:X2}{c.G:X2}{c.B:X2}",
                _ => $"#{c.R:X2}{c.G:X2}{c.B:X2}"
            };

            System.Windows.Clipboard.SetText(value);
            ShowToast($"已复制颜色: {value}");
        }
        catch { }
    }

    private Rect BottomToolbarRect()
    {
        int count = _mode == CaptureMode.Scrolling ? 3 : 16;
        double w = count * 33 + 16;
        double h = 42;
        double x = Math.Max(10, Math.Min(Width - w - 10, _selectionRect.Left + (_selectionRect.Width - w) / 2));
        double y = _selectionRect.Bottom + 12;
        double subExtra = (_isSubToolbarVisible && _mode != CaptureMode.Scrolling && _currentTool != ToolbarTool.None) ? 48 : 0;
        if (y + h + subExtra > Height - 10)
        {
            y = Math.Max(10, _selectionRect.Bottom - h - subExtra - 12);
        }
        return new Rect(x, y, w, h);
    }

    private record ToolButton(ToolbarTool Tool, string Label, string Tip);

    private static readonly ToolButton[] AllTools = new ToolButton[]
    {
        new(ToolbarTool.Rectangle, "矩", "矩形 (R)"),
        new(ToolbarTool.Ellipse, "圆", "椭圆 (O)"),
        new(ToolbarTool.Line, "线", "直线 (L)"),
        new(ToolbarTool.Arrow, "矢", "箭头 (A)"),
        new(ToolbarTool.Brush, "笔", "画笔 (P)"),
        new(ToolbarTool.Mosaic, "马", "马赛克/毛玻璃 (M)"),
        new(ToolbarTool.Text, "文", "文字 (T)"),
        new(ToolbarTool.Step, "①", "步骤序号 (S)"),
        new(ToolbarTool.Pin, "钉", "贴图置顶 (F3)"),
        new(ToolbarTool.Undo, "↩", "上一步撤销 (Ctrl+Z)"),
        new(ToolbarTool.Redo, "↪", "下一步重做 (Ctrl+Y)"),
        new(ToolbarTool.OCR, "识", "文字识别 OCR"),
        new(ToolbarTool.Scrolling, "长", "滚动长截图"),
        new(ToolbarTool.Cancel, "✖", "退出 (Esc)"),
        new(ToolbarTool.Save, "存", "保存文件 (Ctrl+S)"),
        new(ToolbarTool.Copy, "✔", "复制并完成 (Enter)")
    };

    private static readonly ToolButton[] ScrollingTools = new ToolButton[]
    {
        new(ToolbarTool.Cancel, "✖", "退出长截图"),
        new(ToolbarTool.Save, "存", "保存长图"),
        new(ToolbarTool.Copy, "✔", "完成并复制")
    };

    private void DrawBottomToolbar(DrawingContext dc)
    {
        var r = BottomToolbarRect();

        // 柔和白底胶囊与圆角阴影
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 230)), 1), r, 12, 12);

        var tools = _mode == CaptureMode.Scrolling
            ? ScrollingTools
            : AllTools;

        for (int i = 0; i < tools.Length; i++)
        {
            var btnRect = new Rect(r.Left + 8 + i * 33, r.Top + 6, 30, 30);
            bool isSelected = (_currentTool == tools[i].Tool);
            bool isHovered = (_hoveredButtonIndex == i);

            if (isSelected)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(46, 0, 122, 255)), null, btnRect, 6, 6);
            }
            else if (isHovered)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(20, 0, 0, 0)), null, btnRect, 6, 6);
            }

            if (tools[i].Tool == ToolbarTool.OCR)
            {
                var ocrRect = new Rect(btnRect.Left + 2, btnRect.Top + 7, btnRect.Width - 4, btnRect.Height - 14);
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(25, 25, 27)), null, ocrRect, 4, 4);
                var ocrFt = new FormattedText(
                    "OCR",
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    9.5,
                    Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip
                );
                dc.DrawText(ocrFt, new Point(ocrRect.Left + (ocrRect.Width - ocrFt.Width) / 2, ocrRect.Top + (ocrRect.Height - ocrFt.Height) / 2));
                continue;
            }

            var brush = isSelected
                ? new SolidColorBrush(Color.FromRgb(0, 122, 255))
                : (tools[i].Tool == ToolbarTool.Copy
                    ? new SolidColorBrush(Color.FromRgb(22, 163, 74))
                    : (tools[i].Tool == ToolbarTool.Cancel
                        ? new SolidColorBrush(Color.FromRgb(220, 38, 38))
                        : new SolidColorBrush(Color.FromRgb(50, 52, 60))));

            DrawToolVectorIcon(dc, tools[i].Tool, btnRect, brush);
        }

        // 悬停提示气泡
        if (_hoveredButtonIndex >= 0 && _hoveredButtonIndex < tools.Length)
        {
            var btnRect = new Rect(r.Left + 8 + _hoveredButtonIndex * 33, r.Top + 6, 30, 30);
            DrawTooltipBubble(dc, tools[_hoveredButtonIndex].Tip, btnRect);
        }
    }

    private void DrawToolVectorIcon(DrawingContext dc, ToolbarTool tool, Rect btnRect, Brush brush)
    {
        double cx = btnRect.Left + btnRect.Width / 2.0;
        double cy = btnRect.Top + btnRect.Height / 2.0;
        var pen = new Pen(brush, 1.8)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        switch (tool)
        {
            case ToolbarTool.Rectangle:
                // 矩形图标
                dc.DrawRoundedRectangle(null, pen, new Rect(cx - 7.5, cy - 5.5, 15, 11), 1.5, 1.5);
                break;

            case ToolbarTool.Ellipse:
                // 椭圆/圆形图标
                dc.DrawEllipse(null, pen, new Point(cx, cy), 7.2, 6.2);
                break;

            case ToolbarTool.Line:
                // 直线图标 (带两端点)
                dc.DrawLine(pen, new Point(cx - 6.5, cy + 6.5), new Point(cx + 6.5, cy - 6.5));
                dc.DrawEllipse(brush, null, new Point(cx - 6.5, cy + 6.5), 1.8, 1.8);
                dc.DrawEllipse(brush, null, new Point(cx + 6.5, cy - 6.5), 1.8, 1.8);
                break;

            case ToolbarTool.Arrow:
                // 箭头图标
                dc.DrawLine(pen, new Point(cx - 6.5, cy + 6.5), new Point(cx + 6.5, cy - 6.5));
                dc.DrawLine(pen, new Point(cx + 0.5, cy - 6.5), new Point(cx + 6.5, cy - 6.5));
                dc.DrawLine(pen, new Point(cx + 6.5, cy - 6.5), new Point(cx + 6.5, cy - 0.5));
                break;

            case ToolbarTool.Brush:
                // 画笔图标
                dc.PushTransform(new TranslateTransform(cx - 10, cy - 10));
                dc.DrawGeometry(null, pen, Geometry.Parse("M 13.5,3.5 L 16.5,6.5 L 7.5,15.5 L 3.5,16.5 L 4.5,12.5 Z M 11.5,5.5 L 14.5,8.5"));
                dc.Pop();
                break;

            case ToolbarTool.Mosaic:
                // 马赛克网格图标
                var gridRect = new Rect(cx - 7.5, cy - 7.5, 15, 15);
                dc.DrawRoundedRectangle(null, new Pen(brush, 1.4), gridRect, 2, 2);
                double cell = 15.0 / 3.0;
                for (int r = 0; r < 3; r++)
                {
                    for (int c = 0; c < 3; c++)
                    {
                        if ((r + c) % 2 == 0)
                        {
                            dc.DrawRectangle(brush, null, new Rect(gridRect.Left + c * cell + 0.8, gridRect.Top + r * cell + 0.8, cell - 1.6, cell - 1.6));
                        }
                    }
                }
                break;

            case ToolbarTool.Text:
                // 文字 T 图标
                dc.DrawLine(pen, new Point(cx - 6, cy - 6.5), new Point(cx + 6, cy - 6.5));
                dc.DrawLine(pen, new Point(cx - 6, cy - 6.5), new Point(cx - 6, cy - 4.2));
                dc.DrawLine(pen, new Point(cx + 6, cy - 6.5), new Point(cx + 6, cy - 4.2));
                dc.DrawLine(pen, new Point(cx, cy - 6.5), new Point(cx, cy + 6.5));
                dc.DrawLine(pen, new Point(cx - 3, cy + 6.5), new Point(cx + 3, cy + 6.5));
                break;

            case ToolbarTool.Step:
                // 步骤序号圆圈①图标
                dc.DrawEllipse(null, new Pen(brush, 1.6), new Point(cx, cy), 7.5, 7.5);
                var stepPen = new Pen(brush, 1.6) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
                dc.DrawLine(stepPen, new Point(cx - 1.5, cy - 2.2), new Point(cx + 0.5, cy - 4.0));
                dc.DrawLine(stepPen, new Point(cx + 0.5, cy - 4.0), new Point(cx + 0.5, cy + 3.8));
                dc.DrawLine(stepPen, new Point(cx - 2.0, cy + 3.8), new Point(cx + 2.8, cy + 3.8));
                break;

            case ToolbarTool.Pin:
                // 图钉置顶图标
                dc.PushTransform(new TranslateTransform(cx - 10, cy - 10));
                dc.DrawGeometry(brush, null, Geometry.Parse("M 7,3 L 13,3 L 13,4.8 L 11.8,6 L 11.8,10.2 L 14.5,12.5 L 14.5,14 L 10.8,14 L 10.8,18 L 9.2,18 L 9.2,14 L 5.5,14 L 5.5,12.5 L 8.2,10.2 L 8.2,6 L 7,4.8 Z"));
                dc.Pop();
                break;

            case ToolbarTool.Undo:
                // 撤销图标
                dc.PushTransform(new TranslateTransform(cx - 10, cy - 10));
                dc.DrawGeometry(null, pen, Geometry.Parse("M 8,6 L 4,10 L 8,14 M 4.5,10 L 12,10 A 4,4 0 0 1 16,14 L 16,15"));
                dc.Pop();
                break;

            case ToolbarTool.Redo:
                // 重做图标
                dc.PushTransform(new TranslateTransform(cx - 10, cy - 10));
                dc.DrawGeometry(null, pen, Geometry.Parse("M 12,6 L 16,10 L 12,14 M 15.5,10 L 8,10 A 4,4 0 0 0 4,14 L 4,15"));
                dc.Pop();
                break;

            case ToolbarTool.Scrolling:
                // 长截图图标 (长框 + 上下双向滚动箭头)
                dc.PushTransform(new TranslateTransform(cx - 10, cy - 10));
                dc.DrawGeometry(null, new Pen(brush, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round },
                    Geometry.Parse("M 5,6 L 5,3.5 L 15,3.5 L 15,6 M 5,14 L 5,16.5 L 15,16.5 L 15,14 M 10,6.5 L 10,13.5 M 7.8,8.7 L 10,6.3 L 12.2,8.7 M 7.8,11.3 L 10,13.7 L 12.2,11.3"));
                dc.Pop();
                break;

            case ToolbarTool.Cancel:
                // 取消 ✕ 图标
                dc.DrawLine(pen, new Point(cx - 5.5, cy - 5.5), new Point(cx + 5.5, cy + 5.5));
                dc.DrawLine(pen, new Point(cx + 5.5, cy - 5.5), new Point(cx - 5.5, cy + 5.5));
                break;

            case ToolbarTool.Save:
                // 保存下载图标 (向下箭头 + 托盘底座)
                dc.PushTransform(new TranslateTransform(cx - 10, cy - 10));
                dc.DrawGeometry(null, pen, Geometry.Parse("M 10,3.5 L 10,12.5 M 6.8,9.5 L 10,12.8 L 13.2,9.5 M 4.5,13.5 L 4.5,16.2 L 15.5,16.2 L 15.5,13.5"));
                dc.Pop();
                break;

            case ToolbarTool.Copy:
                // 完成复制 ✓ 图标
                var checkPen = new Pen(brush, 2.2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                dc.DrawLine(checkPen, new Point(cx - 6.0, cy + 0.5), new Point(cx - 1.5, cy + 5.0));
                dc.DrawLine(checkPen, new Point(cx - 1.5, cy + 5.0), new Point(cx + 6.5, cy - 4.5));
                break;
        }
    }

    private void DrawTooltipBubble(DrawingContext dc, string text, Rect targetRect)
    {
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10.5,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );

        double bw = ft.Width + 14;
        double bh = 22;
        double bx = targetRect.Left + (targetRect.Width - bw) / 2;
        double by = targetRect.Top - bh - 6;

        var bubbleRect = new Rect(bx, by, bw, bh);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 30, 30, 30)), null, bubbleRect, 4, 4);
        dc.DrawText(ft, new Point(bubbleRect.Left + 7, bubbleRect.Top + (bh - ft.Height) / 2));
    }

    private Rect SubToolbarRect()
    {
        if (!_isSubToolbarVisible || _mode == CaptureMode.Scrolling || _currentTool == ToolbarTool.None) return Rect.Empty;
        var tb = BottomToolbarRect();
        double w = (_currentTool == ToolbarTool.Mosaic) ? 180 : 520;
        double h = 38;
        double x = Math.Max(10, Math.Min(Width - w - 10, tb.Left + (tb.Width - w) / 2));
        // 子工具栏始终弹在主工具栏下方 (解决需求 1)
        double y = tb.Bottom + 8;
        if (y + h > Height - 6)
        {
            y = Math.Max(8, tb.Top - h - 8);
        }
        return new Rect(x, y, w, h);
    }

    private void DrawSubToolbar(DrawingContext dc)
    {
        var r = SubToolbarRect();
        if (r.IsEmpty) return;

        // 白底与微边框
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(225, 225, 230)), 1), r, 10, 10);
        double midY = r.Top + r.Height / 2;

        if (_currentTool == ToolbarTool.Mosaic)
        {
            // 马赛克专属切换: [毛玻璃 | 马赛克]
            DrawSegmented(dc, new[] { "毛玻璃", "马赛克" }, _mosaicIsBlur ? 0 : 1, new Rect(r.Left + 20, midY - 11, 140, 22));
            return;
        }

        // 1. 左侧选项区 (Left Zone: r.Left + 12 ~ r.Left + 128)
        switch (_currentTool)
        {
            case ToolbarTool.Rectangle:
                DrawCheckbox(dc, new Rect(r.Left + 12, midY - 9, 52, 18), _isRectFilled, "实心");
                DrawCheckbox(dc, new Rect(r.Left + 68, midY - 9, 52, 18), _isRectRounded, "圆角");
                break;
            case ToolbarTool.Ellipse:
                DrawCheckbox(dc, new Rect(r.Left + 12, midY - 9, 52, 18), _isOvalFilled, "实心");
                break;
            case ToolbarTool.Arrow:
                DrawCheckbox(dc, new Rect(r.Left + 12, midY - 9, 70, 18), _isArrowCurved, "曲线");
                break;
            case ToolbarTool.Brush:
                DrawSegmented(dc, new[] { "单色", "荧光" }, _isHighlighter ? 1 : 0, new Rect(r.Left + 10, midY - 11, 110, 22));
                break;
            case ToolbarTool.Step:
                DrawSegmented(dc, new[] { "描边", "实心" }, _stepIsFilled ? 1 : 0, new Rect(r.Left + 12, midY - 11, 80, 22));
                break;
            case ToolbarTool.Text:
                DrawSegmented(dc, new[] { "无", "描边", "背景" }, _textStyleIndex, new Rect(r.Left + 10, midY - 11, 94, 22));
                break;
        }

        // 2. 中间调色板 (Middle Zone: r.Left + 136)
        double dotStartX = r.Left + 136;
        var activeColor = (_currentTool == ToolbarTool.Text && _textSelectingSecondary) ? _secondaryColor : _currentColor;

        for (int i = 0; i < SubToolbarColors.Length; i++)
        {
            var dotRect = new Rect(dotStartX + i * 19, midY - 8, 16, 16);
            bool isSelected = (SubToolbarColors[i] == activeColor);

            dc.DrawEllipse(new SolidColorBrush(SubToolbarColors[i]), new Pen(Brushes.LightGray, 1), new Point(dotRect.Left + 8, dotRect.Top + 8), 7, 7);
            if (isSelected)
            {
                dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(0, 122, 255)), 2), new Point(dotRect.Left + 8, dotRect.Top + 8), 9, 9);
            }
        }

        // 3. 右侧粗细/字号与透明度滑块
        double sizeSliderX = r.Left + 316;
        string sizeLabel = (_currentTool == ToolbarTool.Text) ? "字号" : "粗细";
        var labelFt = new FormattedText(
            sizeLabel,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10.5,
            Brushes.Gray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(labelFt, new Point(sizeSliderX, midY - labelFt.Height / 2));

        var sizeTrack = new Rect(sizeSliderX + 28, midY - 2, 50, 4);
        dc.DrawRoundedRectangle(Brushes.LightGray, null, sizeTrack, 2, 2);

        double sizeRatio = Math.Max(0, Math.Min(1, (_currentThickness - 1) / 30.0));
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, new Point(sizeTrack.Left + sizeRatio * sizeTrack.Width, midY), 5, 5);

        // 不透明度
        double opacSliderX = r.Left + 418;
        var opacFt = new FormattedText(
            "透明度",
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            10.5,
            Brushes.Gray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(opacFt, new Point(opacSliderX, midY - opacFt.Height / 2));

        var opacTrack = new Rect(opacSliderX + 38, midY - 2, 50, 4);
        dc.DrawRoundedRectangle(Brushes.LightGray, null, opacTrack, 2, 2);
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, new Point(opacTrack.Left + _currentOpacity * opacTrack.Width, midY), 5, 5);
    }

    private void DrawCheckbox(DrawingContext dc, Rect r, bool isChecked, string title)
    {
        var boxRect = new Rect(r.Left, r.Top + (r.Height - 12) / 2, 12, 12);
        if (isChecked)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, boxRect, 2, 2);
            var checkFt = new FormattedText("✓", System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI Bold"), 9, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(checkFt, new Point(boxRect.Left + 1.5, boxRect.Top));
        }
        else
        {
            dc.DrawRoundedRectangle(Brushes.White, new Pen(Brushes.Gray, 1), boxRect, 2, 2);
        }

        var ft = new FormattedText(title, System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI"), 11, Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(r.Left + 16, r.Top + (r.Height - ft.Height) / 2));
    }

    private void DrawSegmented(DrawingContext dc, string[] items, int selectedIdx, Rect r)
    {
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(240, 240, 243)), null, r, 4, 4);
        double segW = r.Width / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            var segRect = new Rect(r.Left + i * segW, r.Top, segW, r.Height);
            if (i == selectedIdx)
            {
                dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(215, 215, 220)), 1), segRect, 4, 4);
            }
            var ft = new FormattedText(items[i], System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface(i == selectedIdx ? "Segoe UI Bold" : "Segoe UI"), 10.5, i == selectedIdx ? new SolidColorBrush(Color.FromRgb(0, 122, 255)) : Brushes.DimGray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point(segRect.Left + (segRect.Width - ft.Width) / 2, segRect.Top + (segRect.Height - ft.Height) / 2));
        }
    }

    private void DrawMosaics(DrawingContext dc)
    {
        var mosaics = _annotationManager.Items.Where(a => a.Type == ShapeType.Mosaic).ToList();
        if (_inProgressAnnotation?.Type == ShapeType.Mosaic)
        {
            mosaics.Add(_inProgressAnnotation);
        }

        foreach (var m in mosaics)
        {
            var r = m.Bounds;
            if (r.Width <= 2 || r.Height <= 2) continue;

            int bx = Math.Max(0, (int)r.Left);
            int by = Math.Max(0, (int)r.Top);
            int bw = Math.Min(_fullSnapshot.Width - bx, (int)r.Width);
            int bh = Math.Min(_fullSnapshot.Height - by, (int)r.Height);

            using var slice = ScreenCapture.CropBitmap(_fullSnapshot, new System.Drawing.Rectangle(bx, by, bw, bh));
            using var processed = m.MosaicIsBlur ? ApplyBoxBlur(slice, 6) : ApplyPixelate(slice, 10);

            var source = ScreenCapture.ToBitmapSource(processed);
            dc.DrawImage(source, r);
        }
    }

    private static Bitmap ApplyPixelate(Bitmap src, int pixelSize)
    {
        var dst = new Bitmap(src.Width, src.Height);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        int smallW = Math.Max(1, src.Width / pixelSize);
        int smallH = Math.Max(1, src.Height / pixelSize);
        using var small = new Bitmap(smallW, smallH);
        using (var sg = Graphics.FromImage(small))
        {
            sg.InterpolationMode = InterpolationMode.NearestNeighbor;
            sg.DrawImage(src, 0, 0, smallW, smallH);
        }
        g.DrawImage(small, new System.Drawing.Rectangle(0, 0, src.Width, src.Height));
        return dst;
    }

    private static Bitmap ApplyBoxBlur(Bitmap src, int radius)
    {
        var dst = new Bitmap(src.Width, src.Height);
        using var g = Graphics.FromImage(dst);
        g.InterpolationMode = InterpolationMode.Bilinear;

        int smallW = Math.Max(1, src.Width / radius);
        int smallH = Math.Max(1, src.Height / radius);
        using var small = new Bitmap(smallW, smallH);
        using (var sg = Graphics.FromImage(small))
        {
            sg.InterpolationMode = InterpolationMode.Bilinear;
            sg.DrawImage(src, 0, 0, smallW, smallH);
        }
        g.DrawImage(small, new System.Drawing.Rectangle(0, 0, src.Width, src.Height));
        return dst;
    }

    private void DrawAnnotation(DrawingContext dc, Annotation ann)
    {
        var brush = new SolidColorBrush(ann.PrimaryColor) { Opacity = ann.Opacity };
        var pen = new Pen(brush, ann.Thickness);

        switch (ann.Type)
        {
            case ShapeType.Rectangle:
                if (ann.IsFilled)
                {
                    if (ann.IsRounded) dc.DrawRoundedRectangle(brush, null, ann.Bounds, 8, 8);
                    else dc.DrawRectangle(brush, null, ann.Bounds);
                }
                else
                {
                    if (ann.IsRounded) dc.DrawRoundedRectangle(null, pen, ann.Bounds, 8, 8);
                    else dc.DrawRectangle(null, pen, ann.Bounds);
                }
                break;

            case ShapeType.Ellipse:
                var center = new Point(ann.Bounds.Left + ann.Bounds.Width / 2, ann.Bounds.Top + ann.Bounds.Height / 2);
                if (ann.IsFilled) dc.DrawEllipse(brush, null, center, ann.Bounds.Width / 2, ann.Bounds.Height / 2);
                else dc.DrawEllipse(null, pen, center, ann.Bounds.Width / 2, ann.Bounds.Height / 2);
                break;

            case ShapeType.Line:
                dc.DrawLine(pen, ann.StartPoint, ann.EndPoint);
                break;

            case ShapeType.Arrow:
                DrawArrow(dc, pen, brush, ann.StartPoint, ann.EndPoint, ann.IsCurved);
                break;

            case ShapeType.Brush:
                if (ann.Points.Count > 1)
                {
                    var geo = new StreamGeometry();
                    using (var ctx = geo.Open())
                    {
                        ctx.BeginFigure(ann.Points[0], false, false);
                        ctx.PolyLineTo(ann.Points.Skip(1).ToList(), true, true);
                    }
                    var pPen = ann.IsHighlighter ? new Pen(new SolidColorBrush(ann.PrimaryColor) { Opacity = 0.35 }, ann.Thickness * 2.5) : pen;
                    dc.DrawGeometry(null, pPen, geo);
                }
                break;

            case ShapeType.Step:
                if (ann.IsFilled)
                {
                    dc.DrawEllipse(brush, null, ann.StartPoint, 13, 13);
                    var ft = new FormattedText(ann.StepNumber.ToString(), System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI Bold"), 11, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(ft, new Point(ann.StartPoint.X - ft.Width / 2, ann.StartPoint.Y - ft.Height / 2));
                }
                else
                {
                    dc.DrawEllipse(Brushes.White, pen, ann.StartPoint, 13, 13);
                    var ft = new FormattedText(ann.StepNumber.ToString(), System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI Bold"), 11, brush, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                    dc.DrawText(ft, new Point(ann.StartPoint.X - ft.Width / 2, ann.StartPoint.Y - ft.Height / 2));
                }
                break;

            case ShapeType.Text:
                if (!string.IsNullOrEmpty(ann.Text))
                {
                    var ft = new FormattedText(
                        ann.Text,
                        System.Globalization.CultureInfo.CurrentCulture,
                        System.Windows.FlowDirection.LeftToRight,
                        new Typeface("Segoe UI Semibold"),
                        ann.FontSize,
                        brush,
                        VisualTreeHelper.GetDpi(this).PixelsPerDip
                    );

                    var textRect = new Rect(ann.Bounds.Left, ann.Bounds.Top, ft.Width + 8, ft.Height + 4);
                    if (ann.TextStyleIndex == 2) // 背景模式
                    {
                        dc.DrawRoundedRectangle(new SolidColorBrush(ann.SecondaryColor), null, textRect, 3, 3);
                    }
                    else if (ann.TextStyleIndex == 1) // 描边模式
                    {
                        dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(ann.SecondaryColor), 1.5), textRect, 3, 3);
                    }
                    dc.DrawText(ft, new Point(textRect.Left + 4, textRect.Top + 2));
                }
                break;
        }
    }

    private void DrawArrow(DrawingContext dc, Pen pen, Brush brush, Point start, Point end, bool isCurved)
    {
        if (isCurved)
        {
            var mid = new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2 - 30);
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.QuadraticBezierTo(mid, end, true, false);
            }
            dc.DrawGeometry(null, pen, geo);
        }
        else
        {
            dc.DrawLine(pen, start, end);
        }

        var v = end - (isCurved ? new Point((start.X + end.X) / 2, (start.Y + end.Y) / 2 - 30) : start);
        v.Normalize();
        var perp = new Vector(-v.Y, v.X);
        var p1 = end - v * 15 + perp * 6.5;
        var p2 = end - v * 15 - perp * 6.5;

        var headGeo = new StreamGeometry();
        using (var ctx = headGeo.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }
        dc.DrawGeometry(brush, null, headGeo);
    }

    private void DrawScrollingPreviewPanel(DrawingContext dc)
    {
        double cardW = 220;
        double cardH = Math.Min(Height - 60, Math.Max(360, _selectionRect.Height * 1.5));
        double cardX = (_selectionRect.Left >= cardW + 24) ? (_selectionRect.Left - cardW - 16) : (_selectionRect.Right + 16);
        cardX = Math.Max(16, Math.Min(Width - cardW - 16, cardX));
        double cardY = Math.Max(20, Math.Min(Height - cardH - 20, _selectionRect.Top));

        var cardRect = new Rect(cardX, cardY, cardW, cardH);
        dc.DrawRoundedRectangle(Brushes.White, new Pen(new SolidColorBrush(Color.FromRgb(220, 220, 225)), 1), cardRect, 8, 8);

        // 标题栏 "长截图预览"
        var titleFt = new FormattedText("长截图预览", System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI Bold"), 11, new SolidColorBrush(Color.FromRgb(50, 50, 60)), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(titleFt, new Point(cardX + 10, cardY + 8));

        if (_stitcher.HasImage && _stitcher.StitchedLongBitmap != null)
        {
            var imgSource = ScreenCapture.ToBitmapSource(_stitcher.StitchedLongBitmap);
            double innerW = cardW - 20;
            double innerH = cardH - 85;
            double scale = Math.Min(innerW / imgSource.Width, innerH / imgSource.Height);
            double dw = imgSource.Width * scale;
            double dh = imgSource.Height * scale;

            var drawRect = new Rect(cardX + (cardW - dw) / 2, cardY + 28, dw, dh);
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(245, 245, 248)), null, drawRect);
            dc.DrawImage(imgSource, drawRect);
            dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(215, 215, 220)), 1), drawRect);

            // 高度徽章
            var badgeRect = new Rect(cardX + 10, cardY + cardH - 36, cardW - 20, 22);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 35, 35, 40)), null, badgeRect, 4, 4);

            var badgeFt = new FormattedText($"长图高度: {_stitcher.CurrentHeight} px", System.Globalization.CultureInfo.CurrentCulture, System.Windows.FlowDirection.LeftToRight, new Typeface("Segoe UI Semibold"), 10, Brushes.White, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(badgeFt, new Point(badgeRect.Left + (badgeRect.Width - badgeFt.Width) / 2, badgeRect.Top + 3.5));
        }
    }

    #endregion

    #region Hit Testing & Mouse Events

    private HitArea HitTestSelection(Point p)
    {
        if (_selectionRect.IsEmpty || _mode == CaptureMode.Scrolling) return HitArea.None;
        double cornerTol = 20.0;
        double edgeTol = 10.0;

        if (Math.Abs(p.X - _selectionRect.Left) <= cornerTol && Math.Abs(p.Y - _selectionRect.Top) <= cornerTol) return HitArea.TopLeft;
        if (Math.Abs(p.X - _selectionRect.Right) <= cornerTol && Math.Abs(p.Y - _selectionRect.Top) <= cornerTol) return HitArea.TopRight;
        if (Math.Abs(p.X - _selectionRect.Left) <= cornerTol && Math.Abs(p.Y - _selectionRect.Bottom) <= cornerTol) return HitArea.BottomLeft;
        if (Math.Abs(p.X - _selectionRect.Right) <= cornerTol && Math.Abs(p.Y - _selectionRect.Bottom) <= cornerTol) return HitArea.BottomRight;

        if (Math.Abs(p.X - _selectionRect.Left) <= edgeTol && p.Y >= _selectionRect.Top && p.Y <= _selectionRect.Bottom) return HitArea.LeftEdge;
        if (Math.Abs(p.X - _selectionRect.Right) <= edgeTol && p.Y >= _selectionRect.Top && p.Y <= _selectionRect.Bottom) return HitArea.RightEdge;
        if (Math.Abs(p.Y - _selectionRect.Top) <= edgeTol && p.X >= _selectionRect.Left && p.X <= _selectionRect.Right) return HitArea.TopEdge;
        if (Math.Abs(p.Y - _selectionRect.Bottom) <= edgeTol && p.X >= _selectionRect.Left && p.X <= _selectionRect.Right) return HitArea.BottomEdge;

        if (_currentTool == ToolbarTool.None && _selectionRect.Contains(p)) return HitArea.Move;
        return HitArea.None;
    }

    private void UpdateCursor(Point p)
    {
        var area = HitTestSelection(p);
        switch (area)
        {
            case HitArea.TopLeft:
            case HitArea.BottomRight:
                Cursor = Cursors.SizeNWSE;
                break;
            case HitArea.TopRight:
            case HitArea.BottomLeft:
                Cursor = Cursors.SizeNESW;
                break;
            case HitArea.LeftEdge:
            case HitArea.RightEdge:
                Cursor = Cursors.SizeWE;
                break;
            case HitArea.TopEdge:
            case HitArea.BottomEdge:
                Cursor = Cursors.SizeNS;
                break;
            case HitArea.Move:
                Cursor = Cursors.SizeAll;
                break;
            default:
                Cursor = _mode == CaptureMode.Selecting ? Cursors.Cross : Cursors.Arrow;
                break;
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        CommitInlineText();

        if (_overlayMode == OverlayMode.ColorPicker)
        {
            CopyCurrentPixelHex();
            Close();
            return;
        }

        // 1. 右键处理：如果有选区则撤销选区重新框选；如果没有选区则直接关闭退出
        if (e.ChangedButton == MouseButton.Right)
        {
            if (_mode == CaptureMode.Annotating || !_selectionRect.IsEmpty)
            {
                _mode = CaptureMode.Selecting;
                _selectionRect = Rect.Empty;
                _currentTool = ToolbarTool.None;
                _isSubToolbarVisible = false;
                Render();
                return;
            }
            Close();
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;
        CaptureMouse();

        // 2. 双击选区直接完成并复制到剪贴板
        if (e.ClickCount == 2 && !_selectionRect.IsEmpty && _selectionRect.Contains(p))
        {
            FinishAndCopy();
            return;
        }

        if (_mode == CaptureMode.Selecting)
        {
            // 仅在“窗口截图”模式下，跟随鼠标识别窗口并点击即可直接完成截图 (解决需求 2, 4, 5, 6)
            if (_overlayMode == OverlayMode.Window)
            {
                var targetWin = WindowDetectionHelper.FindWindowAtPoint(_detectedWindows, p);
                if (targetWin != null)
                {
                    _selectionRect = targetWin.DipRect;
                }
                else if (_hoveredWindowRect.HasValue)
                {
                    _selectionRect = _hoveredWindowRect.Value;
                }
                else
                {
                    _selectionRect = new Rect(0, 0, Width, Height);
                }

                LastCaptureStore.LastRect = _selectionRect;
                FinishAndCopy();
                return;
            }

            _startPoint = p;
            _selectionRect = Rect.Empty;
            return;
        }

        // Top bar 点击 (圆角与阴影)
        var topBar = TopBarRect();
        if (topBar.Contains(p))
        {
            if (p.X >= topBar.Left + 88 && p.X <= topBar.Left + 145)
            {
                _isDraggingCornerSlider = true;
                UpdateCornerRadius(p.X, topBar.Left + 88, topBar.Left + 143);
                return;
            }
            if (p.X >= topBar.Left + 155)
            {
                _includeShadow = !_includeShadow;
                PreferencesStore.Current.IncludeShadow = _includeShadow;
                PreferencesStore.Save();
                Render();
                return;
            }
        }

        // Sub-toolbar 点击
        var subBar = SubToolbarRect();
        if (_isSubToolbarVisible && subBar.Contains(p))
        {
            HandleSubToolbarClick(p, subBar);
            return;
        }

        // Bottom toolbar 点击
        var bottomBar = BottomToolbarRect();
        if (bottomBar.Contains(p))
        {
            HandleBottomToolbarClick(p, bottomBar);
            return;
        }

        // 选区四角或边框调整 / 移动 (优先级高于内部普通拖拽，确保随时可拖四角再次框选)
        var hit = HitTestSelection(p);
        if (hit != HitArea.None)
        {
            _activeHitArea = hit;
            _dragStartMouse = p;
            _dragStartSelection = _selectionRect;
            return;
        }

        // 文字标注编辑 (双击或单击已有文字)
        if (_currentTool == ToolbarTool.Text && _selectionRect.Contains(p))
        {
            var hitText = _annotationManager.Items.LastOrDefault(a => a.Type == ShapeType.Text && a.HitTest(p));
            if (hitText != null)
            {
                StartInlineTextEdit(hitText.Bounds.TopLeft, hitText);
                return;
            }
            StartInlineTextEdit(p, null);
            return;
        }

        // 标注开始
        if (_currentTool != ToolbarTool.None && _selectionRect.Contains(p))
        {
            _startPoint = p;
            if (_currentTool == ToolbarTool.Step)
            {
                _annotationManager.Add(new Annotation
                {
                    Type = ShapeType.Step,
                    StartPoint = p,
                    StepNumber = _stepCounter++,
                    PrimaryColor = _currentColor,
                    IsFilled = _stepIsFilled
                });
                Render();
            }
            return;
        }

        // 在选区外部按下鼠标且未选中标注工具时，允许重新拖拽框选新区域
        if (!_selectionRect.Contains(p) && _currentTool == ToolbarTool.None && _annotationManager.Items.Count == 0)
        {
            _mode = CaptureMode.Selecting;
            _startPoint = p;
            _selectionRect = Rect.Empty;
            Render();
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        _currentMouse = e.GetPosition(this);

        if (_mode == CaptureMode.Selecting)
        {
            if (_startPoint.HasValue)
            {
                if (_lockedRatioIndex > 0)
                {
                    double ratio = AspectRatios[_lockedRatioIndex];
                    double dx = _currentMouse.X - _startPoint.Value.X;
                    double dy = _currentMouse.Y - _startPoint.Value.Y;
                    double w = Math.Abs(dx);
                    double h = w / ratio;
                    double x = dx >= 0 ? _startPoint.Value.X : _startPoint.Value.X - w;
                    double y = dy >= 0 ? _startPoint.Value.Y : _startPoint.Value.Y - h;
                    _selectionRect = new Rect(x, y, w, h);
                }
                else
                {
                    double x = Math.Min(_startPoint.Value.X, _currentMouse.X);
                    double y = Math.Min(_startPoint.Value.Y, _currentMouse.Y);
                    double w = Math.Abs(_startPoint.Value.X - _currentMouse.X);
                    double h = Math.Abs(_startPoint.Value.Y - _currentMouse.Y);
                    _selectionRect = new Rect(x, y, w, h);
                }
                _hoveredWindowRect = null;
                Render();
                return;
            }
            else if (_overlayMode == OverlayMode.Window)
            {
                // 窗口截图模式：实时跟随鼠标高亮目标窗口
                var winInfo = WindowDetectionHelper.FindWindowAtPoint(_detectedWindows, _currentMouse);
                _hoveredWindowRect = winInfo?.DipRect;
                _hoveredWindowTitle = winInfo?.Title;
                Cursor = Cursors.Hand;
                Render();
                return;
            }
            else
            {
                _hoveredWindowRect = null;
                Cursor = Cursors.Cross;
                Render();
                return;
            }
        }

        if (_isDraggingCornerSlider)
        {
            var topBar = TopBarRect();
            UpdateCornerRadius(_currentMouse.X, topBar.Left + 88, topBar.Left + 143);
            return;
        }

        if (_isDraggingSizeSlider)
        {
            var subBar = SubToolbarRect();
            UpdateSubToolbarThickness(_currentMouse.X, subBar.Left + 344, subBar.Left + 394);
            return;
        }

        if (_isDraggingOpacitySlider)
        {
            var subBar = SubToolbarRect();
            UpdateSubToolbarOpacity(_currentMouse.X, subBar.Left + 456, subBar.Left + 506);
            return;
        }

        // 选区四角与四边自由拖拽调节（支持通过 4 个角任意方向拖动再次框选）
        if (_activeHitArea != HitArea.None)
        {
            double dx = _currentMouse.X - _dragStartMouse.X;
            double dy = _currentMouse.Y - _dragStartMouse.Y;
            var r = _dragStartSelection;

            switch (_activeHitArea)
            {
                case HitArea.Move:
                    double newLeft = Math.Max(0, Math.Min(Width - r.Width, r.Left + dx));
                    double newTop = Math.Max(0, Math.Min(Height - r.Height, r.Top + dy));
                    _selectionRect = new Rect(newLeft, newTop, r.Width, r.Height);
                    break;
                case HitArea.TopLeft:
                    _selectionRect = NormalizeRect(new Point(r.Right, r.Bottom), new Point(r.Left + dx, r.Top + dy));
                    break;
                case HitArea.TopRight:
                    _selectionRect = NormalizeRect(new Point(r.Left, r.Bottom), new Point(r.Right + dx, r.Top + dy));
                    break;
                case HitArea.BottomLeft:
                    _selectionRect = NormalizeRect(new Point(r.Right, r.Top), new Point(r.Left + dx, r.Bottom + dy));
                    break;
                case HitArea.BottomRight:
                    _selectionRect = NormalizeRect(new Point(r.Left, r.Top), new Point(r.Right + dx, r.Bottom + dy));
                    break;
                case HitArea.LeftEdge:
                    _selectionRect = NormalizeRect(new Point(r.Right, r.Top), new Point(r.Left + dx, r.Bottom));
                    break;
                case HitArea.RightEdge:
                    _selectionRect = NormalizeRect(new Point(r.Left, r.Top), new Point(r.Right + dx, r.Bottom));
                    break;
                case HitArea.TopEdge:
                    _selectionRect = NormalizeRect(new Point(r.Left, r.Bottom), new Point(r.Right, r.Top + dy));
                    break;
                case HitArea.BottomEdge:
                    _selectionRect = NormalizeRect(new Point(r.Left, r.Top), new Point(r.Right, r.Bottom + dy));
                    break;
            }
            LastCaptureStore.LastRect = _selectionRect;
            Render();
            return;
        }

        // 标注绘制中
        if (_mode == CaptureMode.Annotating && _startPoint.HasValue && _currentTool != ToolbarTool.None)
        {
            double x = Math.Min(_startPoint.Value.X, _currentMouse.X);
            double y = Math.Min(_startPoint.Value.Y, _currentMouse.Y);
            double w = Math.Abs(_startPoint.Value.X - _currentMouse.X);
            double h = Math.Abs(_startPoint.Value.Y - _currentMouse.Y);
            var b = new Rect(x, y, w, h);

            switch (_currentTool)
            {
                case ToolbarTool.Rectangle:
                    _inProgressAnnotation = new Annotation { Type = ShapeType.Rectangle, Bounds = b, PrimaryColor = _currentColor, Thickness = _currentThickness, Opacity = _currentOpacity, IsFilled = _isRectFilled, IsRounded = _isRectRounded };
                    break;
                case ToolbarTool.Ellipse:
                    _inProgressAnnotation = new Annotation { Type = ShapeType.Ellipse, Bounds = b, PrimaryColor = _currentColor, Thickness = _currentThickness, Opacity = _currentOpacity, IsFilled = _isOvalFilled };
                    break;
                case ToolbarTool.Line:
                    _inProgressAnnotation = new Annotation { Type = ShapeType.Line, StartPoint = _startPoint.Value, EndPoint = _currentMouse, PrimaryColor = _currentColor, Thickness = _currentThickness, Opacity = _currentOpacity };
                    break;
                case ToolbarTool.Arrow:
                    _inProgressAnnotation = new Annotation { Type = ShapeType.Arrow, StartPoint = _startPoint.Value, EndPoint = _currentMouse, PrimaryColor = _currentColor, Thickness = _currentThickness, Opacity = _currentOpacity, IsCurved = _isArrowCurved };
                    break;
                case ToolbarTool.Brush:
                    if (_inProgressAnnotation == null)
                    {
                        _inProgressAnnotation = new Annotation { Type = ShapeType.Brush, PrimaryColor = _currentColor, Thickness = _currentThickness, Opacity = _currentOpacity, IsHighlighter = _isHighlighter };
                    }
                    _inProgressAnnotation.Points.Add(_currentMouse);
                    break;
                case ToolbarTool.Mosaic:
                    _inProgressAnnotation = new Annotation { Type = ShapeType.Mosaic, Bounds = b, MosaicIsBlur = _mosaicIsBlur };
                    break;
            }
            Render();
            return;
        }

        // 更新工具栏 Hover 状态
        var bar = BottomToolbarRect();
        int oldHover = _hoveredButtonIndex;
        if (bar.Contains(_currentMouse))
        {
            _hoveredButtonIndex = (int)((_currentMouse.X - bar.Left - 8) / 33);
        }
        else
        {
            _hoveredButtonIndex = -1;
        }

        if (oldHover != _hoveredButtonIndex)
        {
            Render();
        }

        UpdateCursor(_currentMouse);
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }

        _isDraggingCornerSlider = false;
        _isDraggingSizeSlider = false;
        _isDraggingOpacitySlider = false;
        _activeHitArea = HitArea.None;

        if (_mode == CaptureMode.Selecting)
        {
            _startPoint = null;
            if (_selectionRect.Width >= 5 && _selectionRect.Height >= 5)
            {
                LastCaptureStore.LastRect = _selectionRect;

                if (_overlayMode == OverlayMode.OCR)
                {
                    PerformOCR();
                    return;
                }
                if (_overlayMode == OverlayMode.Translate)
                {
                    PerformTranslate();
                    return;
                }

                _mode = CaptureMode.Annotating;
            }
            else
            {
                _selectionRect = Rect.Empty;
            }
            Render();
            return;
        }

        if (_mode == CaptureMode.Annotating && _inProgressAnnotation != null)
        {
            _annotationManager.Add(_inProgressAnnotation);
            _inProgressAnnotation = null;
            _startPoint = null;
            Render();
        }
    }

    private async void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode == CaptureMode.Scrolling)
        {
            var p = e.GetPosition(this);
            if (_selectionRect.Contains(p))
            {
                e.Handled = true;
                if (_isStitchingInProgress) return;
                _isStitchingInProgress = true;

                try
                {
                    var screenPt = System.Windows.Forms.Cursor.Position;
                    var hwnd = new WindowInteropHelper(this).Handle;

                    // 1. 穿透获取光标下方真实目标窗口句柄 (Chrome/Edge/Word/PDF)
                    int oldExStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
                    SetWindowLong(hwnd, GWL_EXSTYLE, oldExStyle | WS_EX_TRANSPARENT);

                    IntPtr targetHwnd = WindowFromPoint(new System.Drawing.Point(screenPt.X, screenPt.Y));

                    SetWindowLong(hwnd, GWL_EXSTYLE, oldExStyle);

                    if (targetHwnd == IntPtr.Zero || targetHwnd == hwnd)
                    {
                        targetHwnd = App.LastExternalForegroundHwnd;
                    }

                    // 2. 向目标底层窗口发送原生 WM_MOUSEWHEEL 滚轮消息让页面真实滚动
                    if (targetHwnd != IntPtr.Zero)
                    {
                        IntPtr wParam = (IntPtr)(e.Delta << 16);
                        IntPtr lParam = (IntPtr)((screenPt.Y << 16) | (screenPt.X & 0xFFFF));
                        PostMessage(targetHwnd, WM_MOUSEWHEEL, wParam, lParam);
                    }

                    // 3. 等待底层页面渲染滚动位移 (55ms)
                    await System.Threading.Tasks.Task.Delay(55);

                    // 4. 从屏幕设备上下文抓取选区最新的 live 画面切片并拼接 (解决需求 5)
                    var fresh = CaptureCurrentSelectionLive();
                    if (fresh != null)
                    {
                        var res = _stitcher.AppendSlice(fresh);
                        try { fresh.Dispose(); } catch { }
                        if (res.ReachedLimit)
                        {
                            ModernToast.ShowWarning("已达长截图最大高度限制 (32000px)");
                        }
                        else if (res.DidStitch)
                        {
                            Render();
                        }
                    }
                }
                catch { }
                finally
                {
                    _isStitchingInProgress = false;
                }
            }
        }
    }

    #endregion

    #region Toolbar & Actions

    private void HandleBottomToolbarClick(Point p, Rect bar)
    {
        int idx = (int)((p.X - bar.Left - 8) / 33);
        var tools = _mode == CaptureMode.Scrolling
            ? ScrollingTools.Select(t => t.Tool).ToArray()
            : AllTools.Select(t => t.Tool).ToArray();

        if (idx < 0 || idx >= tools.Length) return;
        var clicked = tools[idx];

        switch (clicked)
        {
            case ToolbarTool.Rectangle:
            case ToolbarTool.Ellipse:
            case ToolbarTool.Line:
            case ToolbarTool.Arrow:
            case ToolbarTool.Brush:
            case ToolbarTool.Mosaic:
            case ToolbarTool.Text:
            case ToolbarTool.Step:
                if (_currentTool == clicked)
                {
                    _isSubToolbarVisible = !_isSubToolbarVisible;
                }
                else
                {
                    _currentTool = clicked;
                    _isSubToolbarVisible = true;
                }
                break;
            case ToolbarTool.Pin:
                PinCurrentSelection();
                break;
            case ToolbarTool.Undo:
                _annotationManager.Undo();
                break;
            case ToolbarTool.Redo:
                _annotationManager.Redo();
                break;
            case ToolbarTool.OCR:
                PerformOCR();
                break;
            case ToolbarTool.Scrolling:
                StartScrollingCapture();
                break;
            case ToolbarTool.Cancel:
                Close();
                break;
            case ToolbarTool.Save:
                SaveFinalImage();
                break;
            case ToolbarTool.Copy:
                FinishAndCopy();
                break;
        }
        Render();
    }

    private void HandleSubToolbarClick(Point p, Rect subBar)
    {
        double midY = subBar.Top + subBar.Height / 2;

        if (_currentTool == ToolbarTool.Mosaic)
        {
            var segRect = new Rect(subBar.Left + 20, midY - 11, 140, 22);
            if (segRect.Contains(p))
            {
                _mosaicIsBlur = (p.X < segRect.Left + 70);
                Render();
            }
            return;
        }

        // 1. 左侧选项点击
        switch (_currentTool)
        {
            case ToolbarTool.Rectangle:
                if (new Rect(subBar.Left + 12, midY - 9, 52, 18).Contains(p)) _isRectFilled = !_isRectFilled;
                if (new Rect(subBar.Left + 68, midY - 9, 52, 18).Contains(p)) _isRectRounded = !_isRectRounded;
                break;
            case ToolbarTool.Ellipse:
                if (new Rect(subBar.Left + 12, midY - 9, 52, 18).Contains(p)) _isOvalFilled = !_isOvalFilled;
                break;
            case ToolbarTool.Arrow:
                if (new Rect(subBar.Left + 12, midY - 9, 70, 18).Contains(p)) _isArrowCurved = !_isArrowCurved;
                break;
            case ToolbarTool.Brush:
                var bSeg = new Rect(subBar.Left + 10, midY - 11, 110, 22);
                if (bSeg.Contains(p)) _isHighlighter = (p.X >= bSeg.Left + 55);
                break;
            case ToolbarTool.Step:
                var sSeg = new Rect(subBar.Left + 12, midY - 11, 80, 22);
                if (sSeg.Contains(p)) _stepIsFilled = (p.X >= sSeg.Left + 40);
                break;
            case ToolbarTool.Text:
                var tSeg = new Rect(subBar.Left + 10, midY - 11, 94, 22);
                if (tSeg.Contains(p))
                {
                    _textStyleIndex = (int)((p.X - tSeg.Left) / (tSeg.Width / 3));
                    _textStyleIndex = Math.Max(0, Math.Min(2, _textStyleIndex));
                }
                break;
        }

        // 2. 调色板颜色点击
        double dotStartX = subBar.Left + 136;
        for (int i = 0; i < SubToolbarColors.Length; i++)
        {
            var dotRect = new Rect(dotStartX + i * 19, midY - 8, 16, 16);
            if (dotRect.Contains(p))
            {
                if (_currentTool == ToolbarTool.Text && _textSelectingSecondary)
                {
                    _secondaryColor = SubToolbarColors[i];
                }
                else
                {
                    _currentColor = SubToolbarColors[i];
                }
                break;
            }
        }

        // 3. 粗细/透明度滑块开始拖拽
        if (new Rect(subBar.Left + 344, midY - 6, 50, 12).Contains(p))
        {
            _isDraggingSizeSlider = true;
            UpdateSubToolbarThickness(p.X, subBar.Left + 344, subBar.Left + 394);
        }
        if (new Rect(subBar.Left + 456, midY - 6, 50, 12).Contains(p))
        {
            _isDraggingOpacitySlider = true;
            UpdateSubToolbarOpacity(p.X, subBar.Left + 456, subBar.Left + 506);
        }

        Render();
    }

    private void UpdateCornerRadius(double mouseX, double minX, double maxX)
    {
        double ratio = Math.Max(0, Math.Min(1, (mouseX - minX) / (maxX - minX)));
        _cornerRadius = (int)(ratio * 36);
        PreferencesStore.Current.CornerRadius = _cornerRadius;
        PreferencesStore.Save();
        Render();
    }

    private void UpdateSubToolbarThickness(double mouseX, double minX, double maxX)
    {
        double ratio = Math.Max(0, Math.Min(1, (mouseX - minX) / (maxX - minX)));
        _currentThickness = Math.Max(1, ratio * 30);
        Render();
    }

    private void UpdateSubToolbarOpacity(double mouseX, double minX, double maxX)
    {
        double ratio = Math.Max(0.1, Math.Min(1, (mouseX - minX) / (maxX - minX)));
        _currentOpacity = ratio;
        Render();
    }

    #endregion

    #region Text Inline Editor

    private void StartInlineTextEdit(Point p, Annotation? existing)
    {
        _editingTextAnnotation = existing;
        TxtInlineEditor.Visibility = Visibility.Visible;
        TxtInlineEditor.FontSize = existing?.FontSize ?? 18.0;
        TxtInlineEditor.Foreground = new SolidColorBrush(existing?.PrimaryColor ?? _currentColor);
        TxtInlineEditor.Text = existing?.Text ?? string.Empty;

        Canvas.SetLeft(TxtInlineEditor, p.X);
        Canvas.SetTop(TxtInlineEditor, p.Y);
        TxtInlineEditor.Focus();
        TxtInlineEditor.SelectAll();
    }

    private void CommitInlineText()
    {
        if (TxtInlineEditor.Visibility != Visibility.Visible) return;
        string text = TxtInlineEditor.Text.Trim();
        TxtInlineEditor.Visibility = Visibility.Collapsed;

        if (!string.IsNullOrEmpty(text))
        {
            double x = Canvas.GetLeft(TxtInlineEditor);
            double y = Canvas.GetTop(TxtInlineEditor);

            if (_editingTextAnnotation != null)
            {
                _editingTextAnnotation.Text = text;
            }
            else
            {
                _annotationManager.Add(new Annotation
                {
                    Type = ShapeType.Text,
                    Bounds = new Rect(x, y, 120, 32),
                    Text = text,
                    FontSize = 18.0,
                    PrimaryColor = _currentColor,
                    SecondaryColor = _secondaryColor,
                    TextStyleIndex = _textStyleIndex
                });
            }
        }
        else if (_editingTextAnnotation != null)
        {
            _annotationManager.Remove(_editingTextAnnotation);
        }
        _editingTextAnnotation = null;
        Render();
    }

    private void TxtInlineEditor_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            CommitInlineText();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            TxtInlineEditor.Visibility = Visibility.Collapsed;
            _editingTextAnnotation = null;
            Render();
            e.Handled = true;
        }
    }

    private void TxtInlineEditor_LostFocus(object sender, RoutedEventArgs e)
    {
        CommitInlineText();
    }

    #endregion

    #region Export & Save

    private Bitmap GetCroppedSelectionBitmap()
    {
        double scaleX = (double)_virtualBounds.Width / Width;
        double scaleY = (double)_virtualBounds.Height / Height;

        int sx = Math.Max(0, (int)Math.Round(_selectionRect.X * scaleX));
        int sy = Math.Max(0, (int)Math.Round(_selectionRect.Y * scaleY));
        int sw = Math.Min(_virtualBounds.Width - sx, (int)Math.Round(_selectionRect.Width * scaleX));
        int sh = Math.Min(_virtualBounds.Height - sy, (int)Math.Round(_selectionRect.Height * scaleY));

        return ScreenCapture.CropBitmap(_fullSnapshot, new System.Drawing.Rectangle(sx, sy, Math.Max(1, sw), Math.Max(1, sh)));
    }

    private void CopyCurrentPixelHex()
    {
        try
        {
            double scaleX = (double)_virtualBounds.Width / Width;
            double scaleY = (double)_virtualBounds.Height / Height;
            int px = Math.Max(0, Math.Min(_virtualBounds.Width - 1, (int)Math.Round(_currentMouse.X * scaleX)));
            int py = Math.Max(0, Math.Min(_virtualBounds.Height - 1, (int)Math.Round(_currentMouse.Y * scaleY)));
            var col = _fullSnapshot.GetPixel(px, py);
            string formattedColor = _colorFormatMode switch
            {
                1 => $"rgb({col.R}, {col.G}, {col.B})",
                2 => RgbToHslString(col.R, col.G, col.B),
                3 => $"#{col.R:X2}{col.G:X2}{col.B:X2}FF",
                _ => $"#{col.R:X2}{col.G:X2}{col.B:X2}"
            };
            System.Windows.Clipboard.SetText(formattedColor);
            ModernToast.ShowSuccess($"已复制颜色: {formattedColor}");
        }
        catch
        {
        }
    }

    private Rect NormalizeRect(Point p1, Point p2)
    {
        double x = Math.Max(0, Math.Min(p1.X, p2.X));
        double y = Math.Max(0, Math.Min(p1.Y, p2.Y));
        double right = Math.Min(Width, Math.Max(p1.X, p2.X));
        double bottom = Math.Min(Height, Math.Max(p1.Y, p2.Y));
        double w = Math.Max(16, right - x);
        double h = Math.Max(16, bottom - y);
        return new Rect(x, y, w, h);
    }

    private static Bitmap RenderTargetBitmapToGdiBitmap(RenderTargetBitmap rtb)
    {
        int w = rtb.PixelWidth;
        int h = rtb.PixelHeight;
        int stride = w * 4;
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new System.Drawing.Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        rtb.CopyPixels(Int32Rect.Empty, data.Scan0, h * stride, stride);
        bmp.UnlockBits(data);
        return bmp;
    }

    private Bitmap GenerateFinalComposite()
    {
        if (_mode == CaptureMode.Scrolling && _stitcher.StitchedLongBitmap != null)
        {
            return new Bitmap(_stitcher.StitchedLongBitmap);
        }

        var baseBitmap = GetCroppedSelectionBitmap();

        // 若包含任何标注（矩形、椭圆、线条、箭头、画笔、马赛克/毛玻璃、文字、序号），将标注完整合成为位图 (解决需求 7 改图与标注导出)
        if (_annotationManager.Items.Count > 0)
        {
            try
            {
                int targetW = Math.Max(1, baseBitmap.Width);
                int targetH = Math.Max(1, baseBitmap.Height);
                double dipW = Math.Max(1.0, _selectionRect.Width);
                double dipH = Math.Max(1.0, _selectionRect.Height);

                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                {
                    dc.PushTransform(new ScaleTransform(targetW / dipW, targetH / dipH));
                    dc.PushTransform(new TranslateTransform(-_selectionRect.Left, -_selectionRect.Top));
                    dc.PushClip(new RectangleGeometry(_selectionRect));

                    dc.DrawImage(ImgSnapshot.Source, new Rect(0, 0, Width, Height));
                    DrawMosaics(dc);
                    foreach (var ann in _annotationManager.Items)
                    {
                        if (ann.Type != ShapeType.Mosaic)
                        {
                            DrawAnnotation(dc, ann);
                        }
                    }

                    dc.Pop();
                    dc.Pop();
                    dc.Pop();
                }

                var rtb = new RenderTargetBitmap(targetW, targetH, 96.0, 96.0, PixelFormats.Pbgra32);
                rtb.Render(visual);
                baseBitmap.Dispose();
                baseBitmap = RenderTargetBitmapToGdiBitmap(rtb);
            }
            catch
            {
            }
        }

        var resultBitmap = new Bitmap(baseBitmap.Width, baseBitmap.Height, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(resultBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 1. 裁剪圆角
            if (_cornerRadius > 0)
            {
                using var path = new GraphicsPath();
                int r = Math.Min(Math.Min(baseBitmap.Width, baseBitmap.Height), _cornerRadius * 2);
                if (r > 0)
                {
                    path.AddArc(0, 0, r, r, 180, 90);
                    path.AddArc(baseBitmap.Width - r, 0, r, r, 270, 90);
                    path.AddArc(baseBitmap.Width - r, baseBitmap.Height - r, r, r, 0, 90);
                    path.AddArc(0, baseBitmap.Height - r, r, r, 90, 90);
                    path.CloseFigure();
                    g.SetClip(path);
                }
            }

            g.DrawImage(baseBitmap, 0, 0);
        }

        baseBitmap.Dispose();
        return resultBitmap;
    }

    private void PinCurrentSelection()
    {
        var bmp = GenerateFinalComposite();
        var pinned = new PinnedWindow(bmp, new Point(_selectionRect.Left, _selectionRect.Top));
        pinned.Show();
        Close();
    }

    private async void PerformOCR()
    {
        ShowToast("正在提取选区文字 (OCR)...");
        var bmp = GetCroppedSelectionBitmap();
        string text = await OcrHelper.RecognizeTextAsync(bmp);
        bmp.Dispose();

        if (string.IsNullOrWhiteSpace(text))
        {
            ModernToast.ShowWarning("未在框选区域内识别到文字内容");
            return;
        }

        try
        {
            System.Windows.Clipboard.SetText(text);
        }
        catch { }
        HistoryStore.AddOcr(text);
        ModernToast.ShowSuccess($"已识别文字并复制到剪贴板 ({text.Length} 字)");
        Close();
    }

    private async void PerformTranslate()
    {
        ShowToast("正在识别文字并翻译...");
        var bmp = GetCroppedSelectionBitmap();
        string text = await OcrHelper.RecognizeTextAsync(bmp);
        bmp.Dispose();
        var trans = new TranslationWindow(text);
        trans.Show();
        Close();
    }

    private void StartScrollingCapture()
    {
        _mode = CaptureMode.Scrolling;
        _isSubToolbarVisible = false;

        // 设置从屏幕截图中排除当前覆盖窗体 (WDA_EXCLUDEFROMCAPTURE = 0x00000011)，确保截取的是底层真实页面
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE);
        }
        catch { }

        var initial = CaptureCurrentSelectionLive() ?? GetCroppedSelectionBitmap();
        _stitcher.Initialize(initial);
        Render();
        ModernToast.ShowInfo("已开启长截图：在框选区域内滚动滚轮即可拼接，点击对勾保存。");
    }

    private Bitmap? CaptureCurrentSelectionLive()
    {
        try
        {
            double scaleX = (double)_virtualBounds.Width / Width;
            double scaleY = (double)_virtualBounds.Height / Height;

            int sx = _virtualBounds.X + (int)Math.Round(_selectionRect.X * scaleX);
            int sy = _virtualBounds.Y + (int)Math.Round(_selectionRect.Y * scaleY);
            int sw = (int)Math.Round(_selectionRect.Width * scaleX);
            int sh = (int)Math.Round(_selectionRect.Height * scaleY);

            if (sw <= 0 || sh <= 0) return null;

            return ScreenCapture.CaptureScreen(new System.Drawing.Rectangle(sx, sy, sw, sh));
        }
        catch
        {
            return null;
        }
    }

    private void CleanupScrollingCapture()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            SetWindowDisplayAffinity(hwnd, 0);
        }
        catch { }
    }

    private static string SaveImageToConfiguredDir(Bitmap img)
    {
        string saveDir = PreferencesStore.GetValidSaveDir();
        string fmt = (PreferencesStore.ImageFormat ?? "PNG").ToUpperInvariant();
        string ext = fmt == "JPG" ? ".jpg" : ".png";
        ImageFormat imgFmt = fmt == "JPG" ? ImageFormat.Jpeg : ImageFormat.Png;
        string filePath = Path.Combine(saveDir, $"ShotX_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
        img.Save(filePath, imgFmt);
        return filePath;
    }

    private void FinishAndCopy()
    {
        CommitInlineText();
        var finalImage = GenerateFinalComposite();
        try
        {
            System.Windows.Clipboard.SetImage(ScreenCapture.ToBitmapSource(finalImage));
        }
        catch { }
        try
        {
            SaveImageToConfiguredDir(finalImage);
        }
        catch { }
        HistoryStore.AddCapture(finalImage, _mode == CaptureMode.Scrolling ? "长截图" : "截图");
        finalImage.Dispose();
        CleanupScrollingCapture();
        ModernToast.ShowSuccess(_mode == CaptureMode.Scrolling ? "长截图已复制并保存" : "截图已复制并保存");
        Close();
    }

    private void SaveFinalImage()
    {
        CommitInlineText();
        var finalImage = GenerateFinalComposite();
        try
        {
            System.Windows.Clipboard.SetImage(ScreenCapture.ToBitmapSource(finalImage));
        }
        catch { }
        try
        {
            string savedPath = SaveImageToConfiguredDir(finalImage);
            HistoryStore.AddCapture(finalImage, _mode == CaptureMode.Scrolling ? "长截图" : "截图");
            CleanupScrollingCapture();
            ModernToast.ShowSuccess($"已保存至: {System.IO.Path.GetFileName(savedPath)}");
        }
        catch (Exception ex)
        {
            ModernToast.ShowError($"保存截图失败: {ex.Message}");
        }
        finally
        {
            finalImage.Dispose();
            Close();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        // Tab / C / 空格键循环切换颜色格式 (HEX, RGB, HSL, HEXA) - 解决需求 2
        if ((_overlayMode == OverlayMode.ColorPicker || _mode == CaptureMode.Selecting) &&
            (e.Key == Key.Tab || e.Key == Key.C || e.Key == Key.Space))
        {
            _colorFormatMode = (_colorFormatMode + 1) % 4;
            string formatName = _colorFormatMode switch
            {
                1 => "RGB",
                2 => "HSL",
                3 => "HEXA",
                _ => "HEX"
            };
            ShowToast($"取色格式已切换为: {formatName}");
            Render();
            e.Handled = true;
            return;
        }

        if (_mode == CaptureMode.Selecting && Keyboard.Modifiers == ModifierKeys.None)
        {
            if (e.Key == Key.D)
            {
                if (LastCaptureStore.HasPrevious)
                {
                    _selectionRect = LastCaptureStore.LastRect ?? Rect.Empty;
                    _mode = CaptureMode.Annotating;
                    ShowToast("已载入上一次截图区域");
                    Render();
                    return;
                }
            }
            else if (e.Key == Key.F)
            {
                _selectionRect = new Rect(0, 0, Width, Height);
                _mode = CaptureMode.Annotating;
                ShowToast("已选择全屏");
                Render();
                return;
            }
            else if ((e.Key >= Key.D0 && e.Key <= Key.D9) || (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9))
            {
                int num = (e.Key >= Key.NumPad0) ? (e.Key - Key.NumPad0) : (e.Key - Key.D0);
                _lockedRatioIndex = num;
                ShowToast(num == 0 ? "已恢复自由选区比例" : $"已锁定比例: {AspectRatioLabels[num]}");
                Render();
                return;
            }
            else if (e.Key == Key.R)
            {
                CopyCurrentColor("RGB");
                return;
            }
            else if (e.Key == Key.H || e.Key == Key.C)
            {
                CopyCurrentColor("HEX");
                return;
            }
            else if (e.Key == Key.Z)
            {
                CopyCurrentColor("NAME");
                return;
            }
        }

        if (e.Key == Key.Enter)
        {
            FinishAndCopy();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Z)
        {
            _annotationManager.Undo();
            Render();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.Y)
        {
            _annotationManager.Redo();
            Render();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.C)
        {
            FinishAndCopy();
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control && e.Key == Key.S)
        {
            SaveFinalImage();
        }
        else if (TxtInlineEditor.Visibility != Visibility.Visible && Keyboard.Modifiers == ModifierKeys.None)
        {
            switch (e.Key)
            {
                case Key.R: _currentTool = ToolbarTool.Rectangle; _isSubToolbarVisible = true; Render(); break;
                case Key.O: _currentTool = ToolbarTool.Ellipse; _isSubToolbarVisible = true; Render(); break;
                case Key.L: _currentTool = ToolbarTool.Line; _isSubToolbarVisible = true; Render(); break;
                case Key.A: _currentTool = ToolbarTool.Arrow; _isSubToolbarVisible = true; Render(); break;
                case Key.P: _currentTool = ToolbarTool.Brush; _isSubToolbarVisible = true; Render(); break;
                case Key.M: _currentTool = ToolbarTool.Mosaic; _isSubToolbarVisible = true; Render(); break;
                case Key.T: _currentTool = ToolbarTool.Text; _isSubToolbarVisible = true; Render(); break;
                case Key.S: _currentTool = ToolbarTool.Step; _isSubToolbarVisible = true; Render(); break;
                case Key.F3: PinCurrentSelection(); break;
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _fullSnapshot.Dispose();
        _stitcher.Reset();
    }

    #endregion
}
