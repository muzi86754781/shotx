using System;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using ShotX.Windows.Core;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using Cursors = System.Windows.Input.Cursors;
using FlowDirection = System.Windows.FlowDirection;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace ShotX.Windows.Views;

public partial class ScreenRecordingPrepWindow : Window
{
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

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
    private static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);
    private const uint SWP_SHOWWINDOW = 0x0040;

    private readonly Rectangle _screenBounds;
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    private Rect _recordRect = Rect.Empty;
    private bool _isSelectingNew = false;
    private Point? _dragStart = null;
    private Point _currentMouse;
    private HitArea _activeHitArea = HitArea.None;
    private Point _hitStartMouse;
    private Rect _hitStartRect;

    // 录制配置开关 (5 项图标：麦克风、系统声音、显示光标、高亮鼠标点击、摄像头(区域左下角))
    public bool EnableMic { get; set; } = false;
    public bool EnableSpeaker { get; set; } = false;
    public bool EnableCursor { get; set; } = true;
    public bool EnableClickHighlight { get; set; } = false;
    public bool EnableWebcam { get; set; } = false;
    public bool RecordAsGif { get; set; } = false;

    private WebcamOverlayWindow? _webcamWindow;
    private bool _handingOffWebcam = false;

    private int _hoveredActionIndex = -1; // 0: 麦克风, 1: 系统声音, 2: 显示光标, 3: 高亮鼠标点击, 4: 摄像头(区域左下角), 5: GIF, 6: Video

    public ScreenRecordingPrepWindow()
    {
        InitializeComponent();

        // 仅绑定到当前鼠标所在的显示器屏幕
        var currentScreen = ScreenCapture.GetCurrentMouseScreen();
        _screenBounds = currentScreen.Bounds;
        (_dpiScaleX, _dpiScaleY) = ScreenCapture.GetMonitorDpiScale(_screenBounds);

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = _screenBounds.Left / _dpiScaleX;
        Top = _screenBounds.Top / _dpiScaleY;
        Width = _screenBounds.Width / _dpiScaleX;
        Height = _screenBounds.Height / _dpiScaleY;

        // 初始为空选区，让用户直接用鼠标自由框选区域；框选后可通过 4 个角任意拖动再次框选
        _recordRect = Rect.Empty;
        Cursor = Cursors.Cross;

        OverlayCanvas.CustomRender = DrawCanvas;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, HWND_TOPMOST, _screenBounds.Left, _screenBounds.Top, _screenBounds.Width, _screenBounds.Height, SWP_SHOWWINDOW);
            }
            var dpi = VisualTreeHelper.GetDpi(this);
            _dpiScaleX = dpi.DpiScaleX;
            _dpiScaleY = dpi.DpiScaleY;
            Width = _screenBounds.Width / _dpiScaleX;
            Height = _screenBounds.Height / _dpiScaleY;
        };

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            OverlayCanvas.InvalidateVisual();
        };

        Closed += (_, _) =>
        {
            if (!_handingOffWebcam && _webcamWindow != null)
            {
                try { _webcamWindow.Close(); } catch { }
                _webcamWindow = null;
            }
        };
    }

    private void DrawCanvas(DrawingContext dc)
    {
        var fullRect = new Rect(0, 0, Width, Height);
        // 铺底 Alpha=1 透明层，确保透明镂空区域内依然 100% 捕获鼠标事件而不穿透到底层桌面
        dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)), null, fullRect);

        // 1. 半透明黑色遮罩
        var maskGeometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(fullRect),
            new RectangleGeometry(_recordRect.IsEmpty ? Rect.Empty : _recordRect)
        );
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, maskGeometry);

        if (_recordRect.IsEmpty)
        {
            // 未框选时：绘制十字准星与操作引导卡片
            var crossPen = new Pen(new SolidColorBrush(Color.FromArgb(140, 255, 255, 255)), 1)
            {
                DashStyle = DashStyles.Dash
            };
            dc.DrawLine(crossPen, new Point(0, _currentMouse.Y), new Point(Width, _currentMouse.Y));
            dc.DrawLine(crossPen, new Point(_currentMouse.X, 0), new Point(_currentMouse.X, Height));

            string hint = "按住鼠标左键拖拽框选录屏区域   |   按 F 键选择当前屏幕全屏   |   按 ESC 或右键退出";
            var ftHint = new FormattedText(
                hint,
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                12.5,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            double boxW = ftHint.Width + 32;
            double boxH = 38;
            var hintRect = new Rect((Width - boxW) / 2.0, 36, boxW, boxH);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(220, 24, 26, 32)), new Pen(new SolidColorBrush(Color.FromArgb(70, 255, 255, 255)), 1), hintRect, 8, 8);
            dc.DrawText(ftHint, new Point(hintRect.Left + 16, hintRect.Top + (boxH - ftHint.Height) / 2.0));
            return;
        }

        // 2. 选区边框与蓝色四角手柄 (支持拖拽 4 个角再次框选调整)
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(46, 135, 255)), 2), _recordRect);
        DrawCornerBrackets(dc, _recordRect);

        // 3. 选区下方控制条 (图 4 像素级实现)
        if (!_isSelectingNew && _activeHitArea == HitArea.None)
        {
            DrawRecordingControls(dc);
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

        dc.DrawLine(pen, new Point(r.Left - 1, r.Top + len), new Point(r.Left - 1, r.Top - 1));
        dc.DrawLine(pen, new Point(r.Left - 1, r.Top - 1), new Point(r.Left + len, r.Top - 1));

        dc.DrawLine(pen, new Point(r.Right - len, r.Top - 1), new Point(r.Right + 1, r.Top - 1));
        dc.DrawLine(pen, new Point(r.Right + 1, r.Top - 1), new Point(r.Right + 1, r.Top + len));

        dc.DrawLine(pen, new Point(r.Left - 1, r.Bottom - len), new Point(r.Left - 1, r.Bottom + 1));
        dc.DrawLine(pen, new Point(r.Left - 1, r.Bottom + 1), new Point(r.Left + len, r.Bottom + 1));

        dc.DrawLine(pen, new Point(r.Right - len, r.Bottom + 1), new Point(r.Right + 1, r.Bottom + 1));
        dc.DrawLine(pen, new Point(r.Right + 1, r.Bottom + 1), new Point(r.Right + 1, r.Bottom - len));

        // 4 个角的圆点控制手柄，方便鼠标直接拖拽四角重新调整区域
        var handlePen = new Pen(accentBrush, 2.0);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Left, r.Top), 5.5, 5.5);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Right, r.Top), 5.5, 5.5);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Left, r.Bottom), 5.5, 5.5);
        dc.DrawEllipse(Brushes.White, handlePen, new Point(r.Right, r.Bottom), 5.5, 5.5);
    }

    private HitArea HitTestSelection(Point p)
    {
        if (_recordRect.IsEmpty) return HitArea.None;
        double cornerTol = 20.0;
        double edgeTol = 10.0;

        if (Math.Abs(p.X - _recordRect.Left) <= cornerTol && Math.Abs(p.Y - _recordRect.Top) <= cornerTol) return HitArea.TopLeft;
        if (Math.Abs(p.X - _recordRect.Right) <= cornerTol && Math.Abs(p.Y - _recordRect.Top) <= cornerTol) return HitArea.TopRight;
        if (Math.Abs(p.X - _recordRect.Left) <= cornerTol && Math.Abs(p.Y - _recordRect.Bottom) <= cornerTol) return HitArea.BottomLeft;
        if (Math.Abs(p.X - _recordRect.Right) <= cornerTol && Math.Abs(p.Y - _recordRect.Bottom) <= cornerTol) return HitArea.BottomRight;

        if (Math.Abs(p.X - _recordRect.Left) <= edgeTol && p.Y >= _recordRect.Top && p.Y <= _recordRect.Bottom) return HitArea.LeftEdge;
        if (Math.Abs(p.X - _recordRect.Right) <= edgeTol && p.Y >= _recordRect.Top && p.Y <= _recordRect.Bottom) return HitArea.RightEdge;
        if (Math.Abs(p.Y - _recordRect.Top) <= edgeTol && p.X >= _recordRect.Left && p.X <= _recordRect.Right) return HitArea.TopEdge;
        if (Math.Abs(p.Y - _recordRect.Bottom) <= edgeTol && p.X >= _recordRect.Left && p.X <= _recordRect.Right) return HitArea.BottomEdge;

        if (_recordRect.Contains(p)) return HitArea.Move;
        return HitArea.None;
    }

    private void UpdateCursor(Point p)
    {
        if (_recordRect.IsEmpty)
        {
            Cursor = Cursors.Cross;
            return;
        }

        if (ControlBarTopRect().Contains(p) || ControlBarBottomRect().Contains(p))
        {
            Cursor = Cursors.Hand;
            return;
        }

        switch (HitTestSelection(p))
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
                Cursor = Cursors.Cross;
                break;
        }
    }

    private Rect NormalizeRect(Point p1, Point p2)
    {
        double x = Math.Max(0, Math.Min(p1.X, p2.X));
        double y = Math.Max(0, Math.Min(p1.Y, p2.Y));
        double right = Math.Min(Width, Math.Max(p1.X, p2.X));
        double bottom = Math.Min(Height, Math.Max(p1.Y, p2.Y));
        double w = Math.Max(24, right - x);
        double h = Math.Max(24, bottom - y);
        return new Rect(x, y, w, h);
    }

    private Rect ControlBarTopRect()
    {
        if (_recordRect.IsEmpty) return Rect.Empty;
        double w = 280;
        double h = 38;
        double x = Math.Max(10, Math.Min(Width - w - 10, _recordRect.Left + (_recordRect.Width - w) / 2.0));
        double y = _recordRect.Bottom + 12;
        if (y + h + 80 > Height)
        {
            y = Math.Max(10, _recordRect.Bottom - h - 90);
        }
        return new Rect(x, y, w, h);
    }

    private Rect ControlBarBottomRect()
    {
        if (_recordRect.IsEmpty) return Rect.Empty;
        var top = ControlBarTopRect();
        return new Rect(top.Left, top.Bottom + 6, top.Width, 74);
    }

    private void DrawRecordingControls(DrawingContext dc)
    {
        var topRect = ControlBarTopRect();
        var btmRect = ControlBarBottomRect();
        if (topRect.IsEmpty || btmRect.IsEmpty) return;

        // --- 第一层：工具栏 ---
        var bgBrush = new SolidColorBrush(Color.FromArgb(235, 36, 38, 44));
        var borderPen = new Pen(new SolidColorBrush(Color.FromArgb(50, 255, 255, 255)), 1);
        dc.DrawRoundedRectangle(bgBrush, borderPen, topRect, 8, 8);

        // 尺寸标签
        string sizeText = $"{(int)_recordRect.Width} × {(int)_recordRect.Height}";
        var sizeBgRect = new Rect(topRect.Left + 8, topRect.Top + 6, 76, 26);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, sizeBgRect, 5, 5);
        var ftSize = new FormattedText(
            sizeText,
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            11,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftSize, new Point(sizeBgRect.Left + (sizeBgRect.Width - ftSize.Width) / 2, sizeBgRect.Top + (sizeBgRect.Height - ftSize.Height) / 2));

        // 图标项依次为：麦克风(0), 系统声音(1), 显示光标(2), 高亮鼠标点击(3), 摄像头(区域左下角)(4) (解决需求 4)
        double startX = topRect.Left + 94;
        string[] tips = { "麦克风", "系统声音", "显示光标", "高亮鼠标点击", "摄像头(区域左下角)" };
        bool[] activeStates = { EnableMic, EnableSpeaker, EnableCursor, EnableClickHighlight, EnableWebcam };

        for (int i = 0; i < 5; i++)
        {
            var btnR = new Rect(startX + i * 35, topRect.Top + 5, 28, 28);
            if (activeStates[i])
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, btnR, 5, 5);
            }
            else if (_hoveredActionIndex == i)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, btnR, 5, 5);
            }

            DrawRecordOptionVectorIcon(dc, i, btnR, activeStates[i] ? Brushes.White : new SolidColorBrush(Color.FromArgb(225, 240, 244, 250)));
        }

        // 悬停提示气泡
        if (_hoveredActionIndex >= 0 && _hoveredActionIndex < 5)
        {
            var hoverBtnRect = new Rect(startX + _hoveredActionIndex * 35, topRect.Top + 5, 28, 28);
            var ftTip = new FormattedText(
                tips[_hoveredActionIndex],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.Medium, FontStretches.Normal),
                11,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            double tw = ftTip.Width + 14;
            double th = 22;
            double tx = hoverBtnRect.Left + (hoverBtnRect.Width - tw) / 2.0;
            double ty = topRect.Top - th - 6;
            var tipRect = new Rect(tx, ty, tw, th);
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(235, 24, 26, 32)), null, tipRect, 4, 4);
            dc.DrawText(ftTip, new Point(tipRect.Left + 7, tipRect.Top + (th - ftTip.Height) / 2.0));
        }

        // --- 第二层：录制类型选择面板 (图 4 紧密复刻) ---
        dc.DrawRoundedRectangle(bgBrush, borderPen, btmRect, 8, 8);

        // 选项 1: 录制 GIF
        var gifRect = new Rect(btmRect.Left + 4, btmRect.Top + 4, btmRect.Width - 8, 31);
        if (_hoveredActionIndex == 5)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)), null, gifRect, 6, 6);
        }
        var ftGifTag = new FormattedText(
            "GIF",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            11,
            new SolidColorBrush(Color.FromRgb(160, 160, 165)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftGifTag, new Point(gifRect.Left + 14, gifRect.Top + (gifRect.Height - ftGifTag.Height) / 2));

        var ftGifLabel = new FormattedText(
            "录制 GIF",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
            12,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftGifLabel, new Point(gifRect.Left + 54, gifRect.Top + (gifRect.Height - ftGifLabel.Height) / 2));

        // 选项 2: 录制视频
        var videoRect = new Rect(btmRect.Left + 4, btmRect.Top + 37, btmRect.Width - 8, 33);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, videoRect, 6, 6);

        var ftCamIcon = new FormattedText(
            "📹",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI Emoji"),
            13,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftCamIcon, new Point(videoRect.Left + 12, videoRect.Top + (videoRect.Height - ftCamIcon.Height) / 2));

        var ftVideoLabel = new FormattedText(
            "录制视频",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI, Microsoft YaHei"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
            12.5,
            Brushes.White,
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftVideoLabel, new Point(videoRect.Left + 38, videoRect.Top + (videoRect.Height - ftVideoLabel.Height) / 2));

        var ftEnter = new FormattedText(
            "↩",
            CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),
            14,
            new SolidColorBrush(Color.FromArgb(200, 255, 255, 255)),
            VisualTreeHelper.GetDpi(this).PixelsPerDip
        );
        dc.DrawText(ftEnter, new Point(videoRect.Right - 22, videoRect.Top + (videoRect.Height - ftEnter.Height) / 2));
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            return;
        }

        if (e.Key == Key.F)
        {
            _recordRect = new Rect(0, 0, Width, Height);
            OverlayCanvas.InvalidateVisual();
            return;
        }

        if (e.Key == Key.Enter && !_recordRect.IsEmpty)
        {
            StartRecording(asGif: false);
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            if (!_recordRect.IsEmpty)
            {
                _recordRect = Rect.Empty;
                _isSelectingNew = false;
                _activeHitArea = HitArea.None;
                OverlayCanvas.InvalidateVisual();
                return;
            }
            Close();
            return;
        }

        if (e.ChangedButton != MouseButton.Left) return;

        var p = e.GetPosition(this);
        var topRect = ControlBarTopRect();
        var btmRect = ControlBarBottomRect();

        // 1. 检查第一层图标点击
        if (!_recordRect.IsEmpty && topRect.Contains(p))
        {
            double startX = topRect.Left + 94;
            for (int i = 0; i < 5; i++)
            {
                var btnR = new Rect(startX + i * 35, topRect.Top + 5, 28, 28);
                if (btnR.Contains(p))
                {
                    HandleTopAction(i);
                    OverlayCanvas.InvalidateVisual();
                    return;
                }
            }
            return;
        }

        // 2. 检查第二层录制按钮点击
        if (!_recordRect.IsEmpty && btmRect.Contains(p))
        {
            var gifRect = new Rect(btmRect.Left + 4, btmRect.Top + 4, btmRect.Width - 8, 31);
            var videoRect = new Rect(btmRect.Left + 4, btmRect.Top + 37, btmRect.Width - 8, 33);
            if (gifRect.Contains(p))
            {
                StartRecording(asGif: true);
                return;
            }
            if (videoRect.Contains(p))
            {
                StartRecording(asGif: false);
                return;
            }
            return;
        }

        CaptureMouse();

        // 3. 检查是否命中已有选区的 4 个角或 4 条边或内部移动 (支持拖四角再次框选调整)
        if (!_recordRect.IsEmpty)
        {
            var hit = HitTestSelection(p);
            if (hit != HitArea.None)
            {
                _activeHitArea = hit;
                _hitStartMouse = p;
                _hitStartRect = _recordRect;
                return;
            }
        }

        // 4. 在空白处或外部按下鼠标：直接框选新区域
        _dragStart = p;
        _isSelectingNew = true;
        _recordRect = Rect.Empty;
        OverlayCanvas.InvalidateVisual();
    }

    private void DrawRecordOptionVectorIcon(DrawingContext dc, int index, Rect btnR, System.Windows.Media.Brush brush)
    {
        double cx = btnR.Left + btnR.Width / 2.0;
        double cy = btnR.Top + btnR.Height / 2.0;
        var pen = new Pen(brush, 1.6)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };

        dc.PushTransform(new TranslateTransform(cx - 10, cy - 10));
        switch (index)
        {
            case 0: // 麦克风 (mic)
                dc.DrawRoundedRectangle(null, pen, new Rect(7.5, 3, 5, 8.5), 2.5, 2.5);
                dc.DrawGeometry(null, pen, Geometry.Parse("M 5,9.5 A 5,5 0 0 0 15,9.5 M 10,14.5 L 10,17 M 7,17 L 13,17"));
                break;

            case 1: // 系统声音 (speaker.wave.2)
                dc.DrawGeometry(brush, null, Geometry.Parse("M 3.5,8 L 6.5,8 L 10.5,4.5 L 10.5,15.5 L 6.5,12 L 3.5,12 Z"));
                dc.DrawGeometry(null, pen, Geometry.Parse("M 13,7.5 A 3.5,3.5 0 0 1 13,12.5 M 15,5.5 A 6.5,6.5 0 0 1 15,14.5"));
                break;

            case 2: // 显示光标 (cursorarrow)
                dc.DrawGeometry(brush, null, Geometry.Parse("M 5.5,3.5 L 5.5,15.5 L 8.8,12.5 L 11.2,17 L 13,16.2 L 10.6,11.6 L 15,11.6 Z"));
                break;

            case 3: // 高亮鼠标点击 (cursorarrow.click)
                dc.DrawGeometry(null, new Pen(brush, 1.4) { DashStyle = DashStyles.Dot }, Geometry.Parse("M 8.5,3 A 5.5,5.5 0 1 1 3,8.5 A 5.5,5.5 0 0 1 8.5,3"));
                dc.DrawGeometry(brush, null, Geometry.Parse("M 8,7.5 L 8,16.5 L 10.5,14.2 L 12.4,17.5 L 13.8,16.8 L 11.9,13.5 L 15.2,13.5 Z"));
                break;

            case 4: // 摄像头(区域左下角) (camera)
                dc.DrawRoundedRectangle(null, pen, new Rect(3, 6, 14, 10), 2, 2);
                dc.DrawGeometry(null, pen, Geometry.Parse("M 7,6 L 8.2,4.2 L 11.8,4.2 L 13,6"));
                dc.DrawEllipse(null, pen, new Point(10, 11), 2.6, 2.6);
                break;
        }
        dc.Pop();
    }

    private void HandleTopAction(int index)
    {
        switch (index)
        {
            case 0:
                EnableMic = !EnableMic;
                break;
            case 1:
                EnableSpeaker = !EnableSpeaker;
                break;
            case 2:
                EnableCursor = !EnableCursor;
                break;
            case 3:
                EnableClickHighlight = !EnableClickHighlight;
                break;
            case 4:
                EnableWebcam = !EnableWebcam;
                ToggleWebcamPreview();
                break;
        }
    }

    private void ToggleWebcamPreview()
    {
        if (EnableWebcam)
        {
            if (_webcamWindow == null && !_recordRect.IsEmpty)
            {
                _webcamWindow = new WebcamOverlayWindow(_recordRect, Left, Top);
                _webcamWindow.Show();
                Activate();
            }
        }
        else
        {
            if (_webcamWindow != null)
            {
                try { _webcamWindow.Close(); } catch { }
                _webcamWindow = null;
            }
        }
    }

    private void StartRecording(bool asGif)
    {
        if (_recordRect.IsEmpty || _recordRect.Width < 10 || _recordRect.Height < 10)
        {
            _recordRect = new Rect(0, 0, Width, Height);
        }

        int physX = _screenBounds.Left + (int)Math.Round(_recordRect.Left * _dpiScaleX);
        int physY = _screenBounds.Top + (int)Math.Round(_recordRect.Top * _dpiScaleY);
        int physW = Math.Max(16, (int)Math.Round(_recordRect.Width * _dpiScaleX));
        int physH = Math.Max(16, (int)Math.Round(_recordRect.Height * _dpiScaleY));
        var captureBounds = new Rectangle(physX, physY, physW, physH);

        WebcamOverlayWindow? handoffWebcam = null;
        if (EnableWebcam && _webcamWindow != null)
        {
            _handingOffWebcam = true;
            handoffWebcam = _webcamWindow;
            _webcamWindow.SetInteractive(false);
        }

        Close();
        var win = new ScreenRecordingWindow(
            asGif,
            captureBounds,
            EnableMic,
            EnableSpeaker,
            EnableCursor,
            EnableClickHighlight,
            handoffWebcam
        );
        win.Show();
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        _currentMouse = e.GetPosition(this);

        // 1. 正在拖动 4 个角、边或整体移动选区
        if (_activeHitArea != HitArea.None)
        {
            double dx = _currentMouse.X - _hitStartMouse.X;
            double dy = _currentMouse.Y - _hitStartMouse.Y;
            var r = _hitStartRect;

            switch (_activeHitArea)
            {
                case HitArea.Move:
                    double nl = Math.Max(0, Math.Min(Width - r.Width, r.Left + dx));
                    double nt = Math.Max(0, Math.Min(Height - r.Height, r.Top + dy));
                    _recordRect = new Rect(nl, nt, r.Width, r.Height);
                    break;
                case HitArea.TopLeft:
                    _recordRect = NormalizeRect(new Point(r.Right, r.Bottom), new Point(r.Left + dx, r.Top + dy));
                    break;
                case HitArea.TopRight:
                    _recordRect = NormalizeRect(new Point(r.Left, r.Bottom), new Point(r.Right + dx, r.Top + dy));
                    break;
                case HitArea.BottomLeft:
                    _recordRect = NormalizeRect(new Point(r.Right, r.Top), new Point(r.Left + dx, r.Bottom + dy));
                    break;
                case HitArea.BottomRight:
                    _recordRect = NormalizeRect(new Point(r.Left, r.Top), new Point(r.Right + dx, r.Bottom + dy));
                    break;
                case HitArea.LeftEdge:
                    _recordRect = NormalizeRect(new Point(r.Right, r.Top), new Point(r.Left + dx, r.Bottom));
                    break;
                case HitArea.RightEdge:
                    _recordRect = NormalizeRect(new Point(r.Left, r.Top), new Point(r.Right + dx, r.Bottom));
                    break;
                case HitArea.TopEdge:
                    _recordRect = NormalizeRect(new Point(r.Left, r.Bottom), new Point(r.Right, r.Top + dy));
                    break;
                case HitArea.BottomEdge:
                    _recordRect = NormalizeRect(new Point(r.Left, r.Top), new Point(r.Right, r.Bottom + dy));
                    break;
            }
            OverlayCanvas.InvalidateVisual();
            return;
        }

        // 2. 正在框选新区域
        if (_isSelectingNew && _dragStart.HasValue)
        {
            double x = Math.Min(_dragStart.Value.X, _currentMouse.X);
            double y = Math.Min(_dragStart.Value.Y, _currentMouse.Y);
            double w = Math.Abs(_dragStart.Value.X - _currentMouse.X);
            double h = Math.Abs(_dragStart.Value.Y - _currentMouse.Y);
            if (w > 8 && h > 8)
            {
                _recordRect = new Rect(x, y, w, h);
            }
            OverlayCanvas.InvalidateVisual();
            return;
        }

        UpdateCursor(_currentMouse);

        if (_recordRect.IsEmpty)
        {
            OverlayCanvas.InvalidateVisual();
            return;
        }

        // 3. 检测控制栏按钮悬停
        var topRect = ControlBarTopRect();
        var btmRect = ControlBarBottomRect();
        int newHover = -1;

        if (topRect.Contains(_currentMouse))
        {
            double startX = topRect.Left + 94;
            for (int i = 0; i < 5; i++)
            {
                var btnR = new Rect(startX + i * 35, topRect.Top + 5, 28, 28);
                if (btnR.Contains(_currentMouse))
                {
                    newHover = i;
                    break;
                }
            }
        }
        else if (btmRect.Contains(_currentMouse))
        {
            var gifRect = new Rect(btmRect.Left + 4, btmRect.Top + 4, btmRect.Width - 8, 31);
            if (gifRect.Contains(_currentMouse)) newHover = 5;
            else newHover = 6;
        }

        if (newHover != _hoveredActionIndex)
        {
            _hoveredActionIndex = newHover;
            OverlayCanvas.InvalidateVisual();
        }
    }

    private void Window_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (IsMouseCaptured)
        {
            ReleaseMouseCapture();
        }
        _isSelectingNew = false;
        _dragStart = null;
        _activeHitArea = HitArea.None;
        OverlayCanvas.InvalidateVisual();
    }
}
