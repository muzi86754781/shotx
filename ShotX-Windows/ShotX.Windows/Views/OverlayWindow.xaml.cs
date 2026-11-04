using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Forms;
using System.Windows.Input;
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

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        _fullSnapshot = ScreenCapture.CaptureAllScreens(out _virtualBounds);
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
            _selectionRect = initialRect.Value;
            _mode = CaptureMode.Annotating;
        }

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Render();
        };
    }

    #region Rendering

    private void Render()
    {
        OverlayCanvas.InvalidateVisual();
    }

    private void DrawCanvas(DrawingContext dc)
    {
        // 1. 绘制半透明灰色遮罩
        var fullRect = new Rect(0, 0, Width, Height);
        var maskGeometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(fullRect),
            new RectangleGeometry(_selectionRect.IsEmpty ? Rect.Empty : _selectionRect)
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
            else
            {
                // 十字准星贯穿全屏 (图 2)
                var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(130, 255, 255, 255)), 1)
                {
                    DashStyle = DashStyles.Dash
                };
                dc.DrawLine(crossPen, new Point(0, _currentMouse.Y), new Point(Width, _currentMouse.Y));
                dc.DrawLine(crossPen, new Point(_currentMouse.X, 0), new Point(_currentMouse.X, Height));

                if (_hoveredWindowRect.HasValue)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 0, 122, 255)), new Pen(new SolidColorBrush(Color.FromRgb(0, 122, 255)), 2), _hoveredWindowRect.Value);
                }
                DrawLoupe(dc, _currentMouse);
                DrawQuickHelpCard(dc);
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
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(46, 135, 255)), 3.5)
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

    private void DrawLoupe(DrawingContext dc, Point p)
    {
        double loupeSize = 136;
        double x = p.X + 22;
        double y = p.Y + 22;
        if (x + loupeSize > Width) x = p.X - loupeSize - 22;
        if (y + loupeSize + 46 > Height) y = p.Y - loupeSize - 46;

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
        var infoRect = new Rect(x, y + loupeSize + 4, loupeSize, 40);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 25, 27, 32)), null, infoRect, 6, 6);

        // 当前颜色方块
        var colorBox = new Rect(infoRect.Left + 8, infoRect.Top + 8, 14, 14);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B)), new Pen(Brushes.White, 1), colorBox, 2, 2);

        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        string colorName = GetColorName(c);
        var ftHex = new FormattedText(
            $"{hex} {colorName}",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas, Microsoft YaHei"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            10.5,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftHex, new Point(infoRect.Left + 26, infoRect.Top + 7));

        var ftRgb = new FormattedText(
            $"RGB: ({c.R}, {c.G}, {c.B})",
            System.Globalization.CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            9.5,
            new SolidColorBrush(Color.FromRgb(180, 185, 195)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftRgb, new Point(infoRect.Left + 8, infoRect.Top + 24));
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
        if (y + h > Height - 10)
        {
            y = _selectionRect.Bottom - h - 12;
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
        new(ToolbarTool.OCR, "识", "离线文字识别 OCR"),
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

            var brush = isSelected ? new SolidColorBrush(Color.FromRgb(0, 122, 255)) : (tools[i].Tool == ToolbarTool.Copy ? Brushes.Green : new SolidColorBrush(Color.FromRgb(60, 60, 67)));
            var ft = new FormattedText(
                tools[i].Label,
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                13,
                brush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            dc.DrawText(ft, new Point(btnRect.Left + (btnRect.Width - ft.Width) / 2, btnRect.Top + (btnRect.Height - ft.Height) / 2));
        }

        // 悬停提示气泡
        if (_hoveredButtonIndex >= 0 && _hoveredButtonIndex < tools.Length)
        {
            var btnRect = new Rect(r.Left + 8 + _hoveredButtonIndex * 33, r.Top + 6, 30, 30);
            DrawTooltipBubble(dc, tools[_hoveredButtonIndex].Tip, btnRect);
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
        double y = tb.Top - h - 8;
        if (y < 8)
        {
            y = tb.Bottom + 8;
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
        double tol = 12.0;

        if (Math.Abs(p.X - _selectionRect.Left) <= tol && Math.Abs(p.Y - _selectionRect.Top) <= tol) return HitArea.TopLeft;
        if (Math.Abs(p.X - _selectionRect.Right) <= tol && Math.Abs(p.Y - _selectionRect.Top) <= tol) return HitArea.TopRight;
        if (Math.Abs(p.X - _selectionRect.Left) <= tol && Math.Abs(p.Y - _selectionRect.Bottom) <= tol) return HitArea.BottomLeft;
        if (Math.Abs(p.X - _selectionRect.Right) <= tol && Math.Abs(p.Y - _selectionRect.Bottom) <= tol) return HitArea.BottomRight;

        if (Math.Abs(p.X - _selectionRect.Left) <= tol && p.Y >= _selectionRect.Top && p.Y <= _selectionRect.Bottom) return HitArea.LeftEdge;
        if (Math.Abs(p.X - _selectionRect.Right) <= tol && p.Y >= _selectionRect.Top && p.Y <= _selectionRect.Bottom) return HitArea.RightEdge;
        if (Math.Abs(p.Y - _selectionRect.Top) <= tol && p.X >= _selectionRect.Left && p.X <= _selectionRect.Right) return HitArea.TopEdge;
        if (Math.Abs(p.Y - _selectionRect.Bottom) <= tol && p.X >= _selectionRect.Left && p.X <= _selectionRect.Right) return HitArea.BottomEdge;

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
                Cursor = Cursors.Arrow;
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
            // 如果悬停在探测到的窗口上且无拖拽，直接吸附该窗口
            if (_hoveredWindowRect.HasValue && _selectionRect.IsEmpty)
            {
                _selectionRect = _hoveredWindowRect.Value;
                _hoveredWindowRect = null;
                _mode = CaptureMode.Annotating;
                LastCaptureStore.LastRect = _selectionRect;
                Render();
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

        // 选区调整或移动
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
            else
            {
                // 窗口探测与吸附
                double scaleX = (double)_virtualBounds.Width / Width;
                double scaleY = (double)_virtualBounds.Height / Height;
                _hoveredWindowRect = WindowDetectionHelper.GetWindowRectAtPoint(_currentMouse, scaleX, scaleY, Left, Top);
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

        // 选区边框拖拽调节
        if (_activeHitArea != HitArea.None)
        {
            double dx = _currentMouse.X - _dragStartMouse.X;
            double dy = _currentMouse.Y - _dragStartMouse.Y;
            var r = _dragStartSelection;

            switch (_activeHitArea)
            {
                case HitArea.Move:
                    _selectionRect = new Rect(r.Left + dx, r.Top + dy, r.Width, r.Height);
                    break;
                case HitArea.TopLeft:
                    _selectionRect = new Rect(Math.Min(r.Right - 10, r.Left + dx), Math.Min(r.Bottom - 10, r.Top + dy), Math.Max(10, r.Width - dx), Math.Max(10, r.Height - dy));
                    break;
                case HitArea.TopRight:
                    _selectionRect = new Rect(r.Left, Math.Min(r.Bottom - 10, r.Top + dy), Math.Max(10, r.Width + dx), Math.Max(10, r.Height - dy));
                    break;
                case HitArea.BottomLeft:
                    _selectionRect = new Rect(Math.Min(r.Right - 10, r.Left + dx), r.Top, Math.Max(10, r.Width - dx), Math.Max(10, r.Height + dy));
                    break;
                case HitArea.BottomRight:
                    _selectionRect = new Rect(r.Left, r.Top, Math.Max(10, r.Width + dx), Math.Max(10, r.Height + dy));
                    break;
                case HitArea.LeftEdge:
                    _selectionRect = new Rect(Math.Min(r.Right - 10, r.Left + dx), r.Top, Math.Max(10, r.Width - dx), r.Height);
                    break;
                case HitArea.RightEdge:
                    _selectionRect = new Rect(r.Left, r.Top, Math.Max(10, r.Width + dx), r.Height);
                    break;
                case HitArea.TopEdge:
                    _selectionRect = new Rect(r.Left, Math.Min(r.Bottom - 10, r.Top + dy), r.Width, Math.Max(10, r.Height - dy));
                    break;
                case HitArea.BottomEdge:
                    _selectionRect = new Rect(r.Left, r.Top, r.Width, Math.Max(10, r.Height + dy));
                    break;
            }
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

    private void Window_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_mode == CaptureMode.Scrolling)
        {
            var p = e.GetPosition(this);
            if (_selectionRect.Contains(p))
            {
                var fresh = GetCroppedSelectionBitmap();
                var res = _stitcher.AppendSlice(fresh);
                if (res.ReachedLimit)
                {
                    System.Windows.MessageBox.Show("已达长截图最大高度限制 (32000px)", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (res.DidStitch)
                {
                    Render();
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
            string hex = $"#{col.R:X2}{col.G:X2}{col.B:X2}";
            System.Windows.Clipboard.SetText(hex);
            System.Windows.MessageBox.Show($"已复制颜色代码: {hex}", "ShotX 拾色器", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch
        {
        }
    }

    private Bitmap GenerateFinalComposite()
    {
        if (_mode == CaptureMode.Scrolling && _stitcher.StitchedLongBitmap != null)
        {
            return new Bitmap(_stitcher.StitchedLongBitmap);
        }

        var baseBitmap = GetCroppedSelectionBitmap();
        var resultBitmap = new Bitmap(baseBitmap.Width, baseBitmap.Height, PixelFormat.Format32bppArgb);

        using (var g = Graphics.FromImage(resultBitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;

            // 1. 裁剪圆角
            if (_cornerRadius > 0)
            {
                using var path = new GraphicsPath();
                int r = _cornerRadius * 2;
                path.AddArc(0, 0, r, r, 180, 90);
                path.AddArc(baseBitmap.Width - r, 0, r, r, 270, 90);
                path.AddArc(baseBitmap.Width - r, baseBitmap.Height - r, r, r, 0, 90);
                path.AddArc(0, baseBitmap.Height - r, r, r, 90, 90);
                path.CloseFigure();
                g.SetClip(path);
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
        var bmp = GetCroppedSelectionBitmap();
        string text = await OcrHelper.RecognizeTextAsync(bmp);
        bmp.Dispose();
        System.Windows.Clipboard.SetText(text);
        HistoryStore.AddOcr(text);
        System.Windows.MessageBox.Show(text, "ShotX 文字识别 (已复制到剪贴板)", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private async void PerformTranslate()
    {
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
        var initial = GetCroppedSelectionBitmap();
        _stitcher.Initialize(initial);
        Render();
    }

    private void FinishAndCopy()
    {
        CommitInlineText();
        var finalImage = GenerateFinalComposite();
        System.Windows.Clipboard.SetImage(ScreenCapture.ToBitmapSource(finalImage));
        HistoryStore.AddCapture(finalImage, _mode == CaptureMode.Scrolling ? "长截图" : "截图");
        if (PreferencesStore.Current.PlaySound)
        {
            System.Media.SystemSounds.Beep.Play();
        }
        finalImage.Dispose();
        Close();
    }

    private void SaveFinalImage()
    {
        CommitInlineText();
        var finalImage = GenerateFinalComposite();
        using var sfd = new SaveFileDialog
        {
            Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|Bitmap Image (*.bmp)|*.bmp",
            FileName = $"ShotX_{DateTime.Now:yyyyMMdd_HHmmss}.png",
            InitialDirectory = PreferencesStore.Current.SavePath
        };
        if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            finalImage.Save(sfd.FileName, ImageFormat.Png);
            HistoryStore.AddCapture(finalImage, _mode == CaptureMode.Scrolling ? "长截图" : "截图");
            if (PreferencesStore.Current.PlaySound)
            {
                System.Media.SystemSounds.Beep.Play();
            }
        }
        finalImage.Dispose();
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
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
