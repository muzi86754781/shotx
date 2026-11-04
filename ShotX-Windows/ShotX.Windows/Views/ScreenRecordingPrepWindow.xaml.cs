using System;
using System.Drawing;
using System.Globalization;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media;
using ShotX.Windows.Core;
using Application = System.Windows.Application;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;
using FlowDirection = System.Windows.FlowDirection;

namespace ShotX.Windows.Views;

public partial class ScreenRecordingPrepWindow : Window
{
    private Rect _recordRect;
    private bool _isDragging = false;
    private Point? _dragStart = null;
    private Point _currentMouse;

    // 录制配置开关
    public bool EnableMic { get; set; } = false;
    public bool EnableSpeaker { get; set; } = true;
    public bool EnableCursor { get; set; } = true;
    public bool EnableCountdown { get; set; } = false;
    public bool RecordAsGif { get; set; } = false;

    private int _hoveredActionIndex = -1; // 0: Mic, 1: Speaker, 2: Cursor, 3: Timer, 4: Snapshot, 5: GIF, 6: Video

    public ScreenRecordingPrepWindow()
    {
        InitializeComponent();

        var primary = Screen.PrimaryScreen ?? Screen.AllScreens[0];
        Left = primary.Bounds.Left;
        Top = primary.Bounds.Top;
        Width = primary.Bounds.Width;
        Height = primary.Bounds.Height;

        // 默认居中一个推荐选区 640 x 420
        double initW = 640;
        double initH = 420;
        double initX = (Width - initW) / 2.0;
        double initY = (Height - initH) / 2.0 - 40;
        _recordRect = new Rect(initX, initY, initW, initH);

        OverlayCanvas.CustomRender = DrawCanvas;
    }

    private void DrawCanvas(DrawingContext dc)
    {
        // 1. 半透明黑色遮罩
        var fullRect = new Rect(0, 0, Width, Height);
        var maskGeometry = new CombinedGeometry(
            GeometryCombineMode.Exclude,
            new RectangleGeometry(fullRect),
            new RectangleGeometry(_recordRect.IsEmpty ? Rect.Empty : _recordRect)
        );
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(120, 0, 0, 0)), null, maskGeometry);

        if (_recordRect.IsEmpty) return;

        // 2. 选区边框与蓝色四角手柄
        dc.DrawRectangle(null, new Pen(new SolidColorBrush(Color.FromRgb(46, 135, 255)), 2), _recordRect);
        DrawCornerBrackets(dc, _recordRect);

        // 3. 选区下方控制条 (图 4 像素级实现)
        DrawRecordingControls(dc);
    }

    private void DrawCornerBrackets(DrawingContext dc, Rect r)
    {
        var pen = new Pen(new SolidColorBrush(Color.FromRgb(46, 135, 255)), 3.5)
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
    }

    private Rect ControlBarTopRect()
    {
        double w = 280;
        double h = 38;
        double x = _recordRect.Left + (_recordRect.Width - w) / 2.0;
        double y = _recordRect.Bottom + 12;
        if (y + h + 80 > Height)
        {
            y = _recordRect.Bottom - h - 90;
        }
        return new Rect(x, y, w, h);
    }

    private Rect ControlBarBottomRect()
    {
        var top = ControlBarTopRect();
        return new Rect(top.Left, top.Bottom + 6, top.Width, 74);
    }

    private void DrawRecordingControls(DrawingContext dc)
    {
        var topRect = ControlBarTopRect();
        var btmRect = ControlBarBottomRect();

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

        // 图标项: 麦克风(0), 扬声器(1), 光标(2), 倒计时(3), 快照(4)
        double startX = topRect.Left + 94;
        string[] icons = { "🎤", "🔊", "🖱️", "⏱️", "📷" };
        bool[] activeStates = { EnableMic, EnableSpeaker, EnableCursor, EnableCountdown, false };

        for (int i = 0; i < 5; i++)
        {
            var btnR = new Rect(startX + i * 35, topRect.Top + 5, 28, 28);
            if (activeStates[i] && i == 2)
            {
                // 光标默认蓝色高亮块 (如同图 4)
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0, 122, 255)), null, btnR, 5, 5);
            }
            else if (activeStates[i])
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)), null, btnR, 5, 5);
            }
            else if (_hoveredActionIndex == i)
            {
                dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), null, btnR, 5, 5);
            }

            var ftIcon = new FormattedText(
                icons[i],
                CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI Emoji"),
                12,
                Brushes.White,
                VisualTreeHelper.GetDpi(this).PixelsPerDip
            );
            dc.DrawText(ftIcon, new Point(btnR.Left + (btnR.Width - ftIcon.Width) / 2, btnR.Top + (btnR.Height - ftIcon.Height) / 2));
        }

        // --- 第二层：录制类型选择面板 (图 4 紧密复刻) ---
        dc.DrawRoundedRectangle(bgBrush, borderPen, btmRect, 8, 8);

        // 选项 1: 录制 GIF
        var gifRect = new Rect(btmRect.Left + 4, btmRect.Top + 4, btmRect.Width - 8, 31);
        if (_hoveredActionIndex == 5)
        {
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)), null, gifRect, 6, 6);
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

        // 选项 2: 录制视频 (当前默认选中项，深蓝色高亮整行)
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

        if (e.Key == Key.Enter)
        {
            StartRecording(asGif: false);
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Right)
        {
            Close();
            return;
        }

        var p = e.GetPosition(this);
        var topRect = ControlBarTopRect();
        var btmRect = ControlBarBottomRect();

        // 检查第一层图标点击
        if (topRect.Contains(p))
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

        // 检查第二层点击
        if (btmRect.Contains(p))
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

        // 选区拖选
        _dragStart = p;
        _isDragging = true;
    }

    private void HandleTopAction(int index)
    {
        switch (index)
        {
            case 0: EnableMic = !EnableMic; break;
            case 1: EnableSpeaker = !EnableSpeaker; break;
            case 2: EnableCursor = !EnableCursor; break;
            case 3: EnableCountdown = !EnableCountdown; break;
            case 4: TakeSnapshot(); break;
        }
    }

    private void TakeSnapshot()
    {
        try
        {
            Close();
            if (Application.Current is App app)
            {
                app.CaptureFullscreen();
            }
        }
        catch { }
    }

    private void StartRecording(bool asGif)
    {
        Close();
        if (Application.Current is App app)
        {
            var win = new ScreenRecordingWindow(asGif, (int)_recordRect.Width, (int)_recordRect.Height);
            win.Show();
        }
    }

    private void Window_MouseMove(object sender, MouseEventArgs e)
    {
        _currentMouse = e.GetPosition(this);

        if (_isDragging && _dragStart.HasValue)
        {
            double x = Math.Min(_dragStart.Value.X, _currentMouse.X);
            double y = Math.Min(_dragStart.Value.Y, _currentMouse.Y);
            double w = Math.Abs(_dragStart.Value.X - _currentMouse.X);
            double h = Math.Abs(_dragStart.Value.Y - _currentMouse.Y);
            if (w > 20 && h > 20)
            {
                _recordRect = new Rect(x, y, w, h);
                OverlayCanvas.InvalidateVisual();
            }
            return;
        }

        // 检测悬停
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
        _isDragging = false;
        _dragStart = null;
    }
}

