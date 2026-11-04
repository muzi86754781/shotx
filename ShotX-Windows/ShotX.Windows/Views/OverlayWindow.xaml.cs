using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ShotX.Windows.Core;
using ShotX.Windows.Models;
using Color = System.Windows.Media.Color;
using Point = System.Windows.Point;

namespace ShotX.Windows.Views;

public partial class OverlayWindow : Window
{
    private enum CaptureMode
    {
        Selecting,
        Annotating,
        Scrolling
    }

    private readonly Bitmap _fullSnapshot;
    private readonly System.Drawing.Rectangle _virtualBounds;
    private CaptureMode _mode = CaptureMode.Selecting;

    private Point? _startPoint;
    private Rect _selectionRect = Rect.Empty;
    private Point _currentMouse;

    private readonly AnnotationManager _annotationManager = new();
    private Annotation? _inProgressAnnotation;
    private ToolbarTool _currentTool = ToolbarTool.None;
    private Color _currentColor = Colors.Red;
    private double _currentThickness = 4.0;
    private bool _isFilled = false;
    private int _stepCounter = 1;

    private readonly ScrollStitcher _stitcher = new();
    private readonly DrawingVisual _drawingVisual = new();

    public OverlayWindow()
    {
        InitializeComponent();

        // 1. 抓取全屏幕物理像素
        _fullSnapshot = ScreenCapture.CaptureAllScreens(out _virtualBounds);
        ImgSnapshot.Source = ScreenCapture.ToBitmapSource(_fullSnapshot);

        Left = _virtualBounds.Left;
        Top = _virtualBounds.Top;
        Width = _virtualBounds.Width;
        Height = _virtualBounds.Height;

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            Render();
        };
    }

    private void Render()
    {
        using var dc = _drawingVisual.RenderOpen();

        // 1. 绘制半透明黑色遮罩
        var fullRect = new Rect(0, 0, Width, Height);
        var maskGeometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(fullRect),
            new RectangleGeometry(_selectionRect.IsEmpty ? Rect.Empty : _selectionRect)
        );
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, maskGeometry);

        if (_mode == CaptureMode.Selecting)
        {
            if (!_selectionRect.IsEmpty)
            {
                // 绘制当前选框边框
                dc.DrawRectangle(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.DodgerBlue, 1.5), _selectionRect);
                DrawCornerBrackets(dc, _selectionRect);
                DrawDimensionPill(dc, _selectionRect);
            }
            else
            {
                // 绘制放大镜 Loupe
                DrawLoupe(dc, _currentMouse);
            }
        }
        else if (_mode == CaptureMode.Annotating)
        {
            // 绘制选区与 4 边角
            dc.DrawRectangle(null, new System.Windows.Media.Pen(System.Windows.Media.Brushes.DodgerBlue, 1.5), _selectionRect);
            DrawCornerBrackets(dc, _selectionRect);
            DrawDimensionPill(dc, _selectionRect);

            // 绘制所有已完成的标注
            foreach (var ann in _annotationManager.Items)
            {
                DrawAnnotation(dc, ann);
            }

            // 绘制正在拖拽中的标注
            if (_inProgressAnnotation != null)
            {
                DrawAnnotation(dc, _inProgressAnnotation);
            }

            // 绘制底部胶囊工具栏
            DrawBottomToolbar(dc);
        }
        else if (_mode == CaptureMode.Scrolling)
        {
            // 绘制长截图预览卡片
            DrawScrollingPreviewPanel(dc);
            DrawBottomToolbar(dc);
        }

        dc.Close();

        OverlayCanvas.Children.Clear();
        var visualHost = new VisualHost(_drawingVisual);
        OverlayCanvas.Children.Add(visualHost);
    }

    private void DrawCornerBrackets(DrawingContext dc, Rect r)
    {
        var pen = new System.Windows.Media.Pen(System.Windows.Media.Brushes.DodgerBlue, 3.5)
        {
            StartLineCap = PenLineCap.Square,
            EndLineCap = PenLineCap.Square
        };
        double len = 16.0;

        // Top-Left
        dc.DrawLine(pen, new Point(r.Left - 1, r.Top + len), new Point(r.Left - 1, r.Top - 1));
        dc.DrawLine(pen, new Point(r.Left - 1, r.Top - 1), new Point(r.Left + len, r.Top - 1));

        // Top-Right
        dc.DrawLine(pen, new Point(r.Right - len, r.Top - 1), new Point(r.Right + 1, r.Top - 1));
        dc.DrawLine(pen, new Point(r.Right + 1, r.Top - 1), new Point(r.Right + 1, r.Top + len));

        // Bottom-Left
        dc.DrawLine(pen, new Point(r.Left - 1, r.Bottom - len), new Point(r.Left - 1, r.Bottom + 1));
        dc.DrawLine(pen, new Point(r.Left - 1, r.Bottom + 1), new Point(r.Left + len, r.Bottom + 1));

        // Bottom-Right
        dc.DrawLine(pen, new Point(r.Right - len, r.Bottom + 1), new Point(r.Right + 1, r.Bottom + 1));
        dc.DrawLine(pen, new Point(r.Right + 1, r.Bottom + 1), new Point(r.Right + 1, r.Bottom - len));
    }

    private void DrawDimensionPill(DrawingContext dc, Rect r)
    {
        string text = $"{(int)r.Width} x {(int)r.Height}";
        var ft = new FormattedText(
            text,
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI Semibold"),
            11,
            System.Windows.Media.Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );

        double pillW = ft.Width + 16;
        double pillH = 22;
        double pillX = r.Left;
        double pillY = r.Top - pillH - 6;
        if (pillY < 10) pillY = r.Top + 8;

        var pillRect = new Rect(pillX, pillY, pillW, pillH);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)), null, pillRect, 4, 4);
        dc.DrawText(ft, new Point(pillRect.Left + 8, pillRect.Top + (pillH - ft.Height) / 2));
    }

    private void DrawLoupe(DrawingContext dc, Point p)
    {
        double loupeSize = 120;
        double x = p.X + 20;
        double y = p.Y + 20;
        if (x + loupeSize > Width) x = p.X - loupeSize - 20;
        if (y + loupeSize + 40 > Height) y = p.Y - loupeSize - 40;

        int pixelX = Math.Max(0, Math.Min(_fullSnapshot.Width - 1, (int)p.X));
        int pixelY = Math.Max(0, Math.Min(_fullSnapshot.Height - 1, (int)p.Y));
        var c = _fullSnapshot.GetPixel(pixelX, pixelY);

        var loupeRect = new Rect(x, y, loupeSize, loupeSize);
        dc.DrawRoundedRectangle(System.Windows.Media.Brushes.White, new System.Windows.Media.Pen(System.Windows.Media.Brushes.Gray, 1), loupeRect, 6, 6);

        // 放大镜中心十字
        var center = new Point(loupeRect.Left + loupeSize / 2, loupeRect.Top + loupeSize / 2);
        dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.DodgerBlue, 1), new Point(center.X - 10, center.Y), new Point(center.X + 10, center.Y));
        dc.DrawLine(new System.Windows.Media.Pen(System.Windows.Media.Brushes.DodgerBlue, 1), new Point(center.X, center.Y - 10), new Point(center.X, center.Y + 10));

        // 底部颜色信息条
        var infoRect = new Rect(x, y + loupeSize + 4, loupeSize, 28);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 30, 30, 30)), null, infoRect, 4, 4);

        string colorHex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        var ft = new FormattedText(
            $"{colorHex}\nRGB:({c.R},{c.G},{c.B})",
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Consolas"),
            9.5,
            System.Windows.Media.Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ft, new Point(infoRect.Left + 6, infoRect.Top + 2));
    }

    private void DrawAnnotation(DrawingContext dc, Annotation ann)
    {
        var brush = new SolidColorBrush(ann.PrimaryColor) { Opacity = ann.Opacity };
        var pen = new System.Windows.Media.Pen(brush, ann.Thickness);

        switch (ann.Type)
        {
            case ShapeType.Rectangle:
                if (ann.IsFilled)
                {
                    dc.DrawRectangle(brush, null, ann.Bounds);
                }
                else
                {
                    dc.DrawRectangle(null, pen, ann.Bounds);
                }
                break;

            case ShapeType.Ellipse:
                var center = new Point(ann.Bounds.Left + ann.Bounds.Width / 2, ann.Bounds.Top + ann.Bounds.Height / 2);
                double rx = ann.Bounds.Width / 2;
                double ry = ann.Bounds.Height / 2;
                if (ann.IsFilled)
                {
                    dc.DrawEllipse(brush, null, center, rx, ry);
                }
                else
                {
                    dc.DrawEllipse(null, pen, center, rx, ry);
                }
                break;

            case ShapeType.Line:
                dc.DrawLine(pen, ann.StartPoint, ann.EndPoint);
                break;

            case ShapeType.Arrow:
                DrawArrow(dc, pen, brush, ann.StartPoint, ann.EndPoint);
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
                    dc.DrawGeometry(null, pen, geo);
                }
                break;

            case ShapeType.Step:
                dc.DrawEllipse(brush, null, ann.StartPoint, 14, 14);
                var ft = new FormattedText(
                    ann.StepNumber.ToString(),
                    System.Globalization.CultureInfo.CurrentCulture,
                    System.Windows.FlowDirection.LeftToRight,
                    new Typeface("Segoe UI Bold"),
                    11,
                    System.Windows.Media.Brushes.White,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip
                );
                dc.DrawText(ft, new Point(ann.StartPoint.X - ft.Width / 2, ann.StartPoint.Y - ft.Height / 2));
                break;
        }
    }

    private void DrawArrow(DrawingContext dc, System.Windows.Media.Pen pen, System.Windows.Media.Brush brush, Point start, Point end)
    {
        dc.DrawLine(pen, start, end);

        var v = end - start;
        v.Normalize();
        var perp = new Vector(-v.Y, v.X);

        double arrowLength = 16.0;
        double arrowWidth = 7.0;

        var p1 = end - v * arrowLength + perp * arrowWidth;
        var p2 = end - v * arrowLength - perp * arrowWidth;

        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            ctx.BeginFigure(end, true, true);
            ctx.LineTo(p1, true, false);
            ctx.LineTo(p2, true, false);
        }
        dc.DrawGeometry(brush, null, geo);
    }

    private Rect BottomToolbarRect()
    {
        double w = 460;
        double h = 42;
        double x = Math.Max(10, Math.Min(Width - w - 10, _selectionRect.Left + (_selectionRect.Width - w) / 2));
        double y = _selectionRect.Bottom + 12;
        if (y + h > Height - 10)
        {
            y = _selectionRect.Bottom - h - 12;
        }
        return new Rect(x, y, w, h);
    }

    private void DrawBottomToolbar(DrawingContext dc)
    {
        var r = BottomToolbarRect();

        // 胶囊白底与阴影
        dc.DrawRoundedRectangle(System.Windows.Media.Brushes.White, new System.Windows.Media.Pen(System.Windows.Media.Brushes.LightGray, 1), r, 10, 10);

        string[] items = _mode == CaptureMode.Scrolling
            ? new[] { "✖ 退出", "✔ 完成" }
            : new[] { "矩形", "椭圆", "箭头", "画笔", "贴图", "OCR", "长截图", "取消", "保存", "复制" };

        double itemW = r.Width / items.Length;
        for (int i = 0; i < items.Length; i++)
        {
            var btnRect = new Rect(r.Left + i * itemW, r.Top, itemW, r.Height);
            var ft = new FormattedText(
                items[i],
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                11,
                System.Windows.Media.Brushes.DimGray,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            dc.DrawText(ft, new Point(btnRect.Left + (btnRect.Width - ft.Width) / 2, btnRect.Top + (btnRect.Height - ft.Height) / 2));
        }
    }

    private void DrawScrollingPreviewPanel(DrawingContext dc)
    {
        double cardW = 220;
        double cardH = Math.Min(Height - 60, Math.Max(360, _selectionRect.Height * 1.5));
        double cardX = _selectionRect.Right + 16;
        if (cardX + cardW > Width - 16)
        {
            cardX = _selectionRect.Left - cardW - 16;
        }
        double cardY = Math.Max(20, _selectionRect.Top);

        var cardRect = new Rect(cardX, cardY, cardW, cardH);
        dc.DrawRoundedRectangle(System.Windows.Media.Brushes.White, new System.Windows.Media.Pen(System.Windows.Media.Brushes.LightGray, 1), cardRect, 8, 8);

        // 标题
        var titleFt = new FormattedText(
            "长截图预览",
            System.Globalization.CultureInfo.CurrentCulture,
            System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI Bold"),
            11,
            System.Windows.Media.Brushes.DarkSlateGray,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(titleFt, new Point(cardX + 12, cardY + 10));

        // 渲染已拼接的长图
        if (_stitcher.HasImage && _stitcher.StitchedLongBitmap != null)
        {
            var imgSource = ScreenCapture.ToBitmapSource(_stitcher.StitchedLongBitmap);
            double innerW = cardW - 20;
            double innerH = cardH - 80;
            double scale = Math.Min(innerW / imgSource.Width, innerH / imgSource.Height);
            double dw = imgSource.Width * scale;
            double dh = imgSource.Height * scale;

            var drawRect = new Rect(cardX + (cardW - dw) / 2, cardY + 34, dw, dh);
            dc.DrawImage(imgSource, drawRect);

            // 高度徽章
            var badgeRect = new Rect(cardX + 10, cardY + cardH - 36, cardW - 20, 22);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 40, 40, 40)), null, badgeRect, 4, 4);

            var badgeFt = new FormattedText(
                $"长图高度: {_stitcher.CurrentHeight} px",
                System.Globalization.CultureInfo.CurrentCulture,
                System.Windows.FlowDirection.LeftToRight,
                new Typeface("Segoe UI Semibold"),
                10,
                System.Windows.Media.Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            dc.DrawText(badgeFt, new Point(badgeRect.Left + (badgeRect.Width - badgeFt.Width) / 2, badgeRect.Top + 3));
        }
    }

    #region Mouse & Keyboard Events

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_mode == CaptureMode.Selecting)
        {
            _startPoint = p;
            _selectionRect = Rect.Empty;
        }
        else if (_mode == CaptureMode.Annotating)
        {
            var tb = BottomToolbarRect();
            if (tb.Contains(p))
            {
                HandleToolbarClick(p, tb);
                return;
            }

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
                        IsFilled = true
                    });
                    Render();
                }
            }
        }
        else if (_mode == CaptureMode.Scrolling)
        {
            var tb = BottomToolbarRect();
            if (tb.Contains(p))
            {
                double mid = tb.Left + tb.Width / 2;
                if (p.X < mid)
                {
                    Close();
                }
                else
                {
                    FinishAndCopy();
                }
            }
        }
    }

    private void Window_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _currentMouse = e.GetPosition(this);

        if (_mode == CaptureMode.Selecting && _startPoint.HasValue)
        {
            double x = Math.Min(_startPoint.Value.X, _currentMouse.X);
            double y = Math.Min(_startPoint.Value.Y, _currentMouse.Y);
            double w = Math.Abs(_startPoint.Value.X - _currentMouse.X);
            double h = Math.Abs(_startPoint.Value.Y - _currentMouse.Y);
            _selectionRect = new Rect(x, y, w, h);
            Render();
            return;
        }

        if (_mode == CaptureMode.Annotating && _startPoint.HasValue && _currentTool != ToolbarTool.None)
        {
            double x = Math.Min(_startPoint.Value.X, _currentMouse.X);
            double y = Math.Min(_startPoint.Value.Y, _currentMouse.Y);
            double w = Math.Abs(_startPoint.Value.X - _currentMouse.X);
            double h = Math.Abs(_startPoint.Value.Y - _currentMouse.Y);

            switch (_currentTool)
            {
                case ToolbarTool.Rectangle:
                    _inProgressAnnotation = new Annotation
                    {
                        Type = ShapeType.Rectangle,
                        Bounds = new Rect(x, y, w, h),
                        PrimaryColor = _currentColor,
                        Thickness = _currentThickness,
                        IsFilled = _isFilled
                    };
                    break;
                case ToolbarTool.Ellipse:
                    _inProgressAnnotation = new Annotation
                    {
                        Type = ShapeType.Ellipse,
                        Bounds = new Rect(x, y, w, h),
                        PrimaryColor = _currentColor,
                        Thickness = _currentThickness,
                        IsFilled = _isFilled
                    };
                    break;
                case ToolbarTool.Line:
                    _inProgressAnnotation = new Annotation
                    {
                        Type = ShapeType.Line,
                        StartPoint = _startPoint.Value,
                        EndPoint = _currentMouse,
                        PrimaryColor = _currentColor,
                        Thickness = _currentThickness
                    };
                    break;
                case ToolbarTool.Arrow:
                    _inProgressAnnotation = new Annotation
                    {
                        Type = ShapeType.Arrow,
                        StartPoint = _startPoint.Value,
                        EndPoint = _currentMouse,
                        PrimaryColor = _currentColor,
                        Thickness = _currentThickness
                    };
                    break;
                case ToolbarTool.Brush:
                    if (_inProgressAnnotation == null)
                    {
                        _inProgressAnnotation = new Annotation
                        {
                            Type = ShapeType.Brush,
                            PrimaryColor = _currentColor,
                            Thickness = _currentThickness
                        };
                    }
                    _inProgressAnnotation.Points.Add(_currentMouse);
                    break;
            }
            Render();
            return;
        }

        if (_mode == CaptureMode.Selecting && !_startPoint.HasValue)
        {
            Render();
        }
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_mode == CaptureMode.Selecting)
        {
            _startPoint = null;
            if (_selectionRect.Width > 10 && _selectionRect.Height > 10)
            {
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
                // 截取当前视口新鲜画面并追加到长截图
                var fresh = GetCroppedSelectionBitmap();
                var result = _stitcher.AppendSlice(fresh);
                if (result.ReachedLimit)
                {
                    System.Windows.MessageBox.Show("已达长截图最大高度限制 (32000px)", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                else if (result.DidStitch)
                {
                    Render();
                }
            }
        }
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
        else if (e.Key == Key.Enter)
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
    }

    #endregion

    private void HandleToolbarClick(Point p, Rect tb)
    {
        string[] items = new[] { "矩形", "椭圆", "箭头", "画笔", "贴图", "OCR", "长截图", "取消", "保存", "复制" };
        double itemW = tb.Width / items.Length;
        int idx = (int)((p.X - tb.Left) / itemW);
        if (idx < 0 || idx >= items.Length) return;

        switch (items[idx])
        {
            case "矩形":
                _currentTool = ToolbarTool.Rectangle;
                break;
            case "椭圆":
                _currentTool = ToolbarTool.Ellipse;
                break;
            case "箭头":
                _currentTool = ToolbarTool.Arrow;
                break;
            case "画笔":
                _currentTool = ToolbarTool.Brush;
                break;
            case "贴图":
                PinCurrentSelection();
                break;
            case "OCR":
                PerformOCR();
                break;
            case "长截图":
                StartScrollingCapture();
                break;
            case "取消":
                Close();
                break;
            case "保存":
                SaveFinalImage();
                break;
            case "复制":
                FinishAndCopy();
                break;
        }
        Render();
    }

    private Bitmap GetCroppedSelectionBitmap()
    {
        int sx = (int)_selectionRect.X;
        int sy = (int)_selectionRect.Y;
        int sw = (int)_selectionRect.Width;
        int sh = (int)_selectionRect.Height;
        return ScreenCapture.CropBitmap(_fullSnapshot, new System.Drawing.Rectangle(sx, sy, sw, sh));
    }

    private void PinCurrentSelection()
    {
        var bmp = GetCroppedSelectionBitmap();
        var pinned = new PinnedWindow(bmp, new Point(_selectionRect.Left, _selectionRect.Top));
        pinned.Show();
        Close();
    }

    private async void PerformOCR()
    {
        var bmp = GetCroppedSelectionBitmap();
        string text = await OcrHelper.RecognizeTextAsync(bmp);
        System.Windows.Clipboard.SetText(text);
        System.Windows.MessageBox.Show(text, "ShotX 文字识别 (已复制到剪贴板)", MessageBoxButton.OK, MessageBoxImage.Information);
        Close();
    }

    private void StartScrollingCapture()
    {
        _mode = CaptureMode.Scrolling;
        var initial = GetCroppedSelectionBitmap();
        _stitcher.Initialize(initial);
        Render();
    }

    private void FinishAndCopy()
    {
        var finalImage = _mode == CaptureMode.Scrolling && _stitcher.StitchedLongBitmap != null
            ? _stitcher.StitchedLongBitmap
            : GetCroppedSelectionBitmap();

        System.Windows.Clipboard.SetImage(ScreenCapture.ToBitmapSource(finalImage));
        Close();
    }

    private void SaveFinalImage()
    {
        var finalImage = _mode == CaptureMode.Scrolling && _stitcher.StitchedLongBitmap != null
            ? _stitcher.StitchedLongBitmap
            : GetCroppedSelectionBitmap();

        using var sfd = new SaveFileDialog
        {
            Filter = "PNG Image (*.png)|*.png|JPEG Image (*.jpg)|*.jpg|Bitmap Image (*.bmp)|*.bmp",
            FileName = $"ShotX_{DateTime.Now:yyyyMMdd_HHmmss}.png"
        };
        if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            finalImage.Save(sfd.FileName, ImageFormat.Png);
        }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _fullSnapshot.Dispose();
        _stitcher.Reset();
    }
}

public class VisualHost : FrameworkElement
{
    private readonly Visual _visual;
    public VisualHost(Visual visual) => _visual = visual;
    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _visual;
}
