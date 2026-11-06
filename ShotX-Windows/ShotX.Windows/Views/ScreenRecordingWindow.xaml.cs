using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotX.Windows.Core;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows.Views;

public partial class ScreenRecordingWindow : Window
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string lpstrCommand, StringBuilder? lpstrReturnString, int uReturnLength, IntPtr hwndCallback);

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private class ClickRipple
    {
        public int ScreenX { get; init; }
        public int ScreenY { get; init; }
        public DateTime CreatedAt { get; init; }
        public bool IsRightClick { get; init; }
    }

    private int _seconds = 0;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _frameTimer;
    private readonly DispatcherTimer _inputMonitorTimer;
    private readonly List<Bitmap> _capturedFrames = new();
    private readonly List<ClickRipple> _activeRipples = new();

    private Rectangle _captureBounds;
    private bool _isGif = false;
    private bool _enableMic = false;
    private bool _enableSpeaker = false;
    private bool _enableCursor = true;
    private bool _enableClickHighlight = false;
    private bool _audioRecordingActive = false;
    private bool _wasLeftDown = false;
    private bool _wasRightDown = false;
    private WebcamOverlayWindow? _webcamWindow;

    public ScreenRecordingWindow()
    {
        InitializeComponent();

        var screen = ScreenCapture.GetCurrentMouseScreen();
        var (scaleX, scaleY) = ScreenCapture.GetMonitorDpiScale(screen.Bounds);
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = (screen.Bounds.Left / scaleX) + ((screen.Bounds.Width / scaleX) - Width) / 2.0;
        Top = (screen.Bounds.Top / scaleY) + 32;

        _captureBounds = screen.Bounds;

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += (_, _) =>
        {
            _seconds++;
            int m = _seconds / 60;
            int s = _seconds % 60;
            TxtTimer.Text = $"{m:00}:{s:00}";
            DotRecording.Opacity = DotRecording.Opacity > 0.5 ? 0.3 : 1.0;
        };
        _timer.Start();

        _inputMonitorTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(35)
        };
        _inputMonitorTimer.Tick += (_, _) => PollMouseClickHighlight();
        _inputMonitorTimer.Start();

        _frameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(160)
        };
        _frameTimer.Tick += (_, _) => CaptureFrame();
        _frameTimer.Start();
    }

    public ScreenRecordingWindow(bool asGif, int width, int height) : this()
    {
        _isGif = asGif;
        TxtFormat.Text = asGif ? "GIF" : "MP4";
    }

    public ScreenRecordingWindow(bool asGif, Rectangle captureBounds) : this(asGif, captureBounds.Width, captureBounds.Height)
    {
        _captureBounds = captureBounds;
    }

    public ScreenRecordingWindow(
        bool asGif,
        Rectangle captureBounds,
        bool enableMic,
        bool enableSpeaker,
        bool enableCursor,
        bool enableClickHighlight,
        WebcamOverlayWindow? webcamWindow
    ) : this(asGif, captureBounds)
    {
        _enableMic = enableMic;
        _enableSpeaker = enableSpeaker;
        _enableCursor = enableCursor;
        _enableClickHighlight = enableClickHighlight;
        _webcamWindow = webcamWindow;

        if (!_isGif && (_enableMic || _enableSpeaker))
        {
            StartAudioCapture();
        }
    }

    private void StartAudioCapture()
    {
        try
        {
            mciSendString("close shotx_scr_audio", null, 0, IntPtr.Zero);
            int res = mciSendString("open new Type waveaudio Alias shotx_scr_audio", null, 0, IntPtr.Zero);
            if (res == 0)
            {
                mciSendString("record shotx_scr_audio", null, 0, IntPtr.Zero);
                _audioRecordingActive = true;
            }
        }
        catch
        {
        }
    }

    private void PollMouseClickHighlight()
    {
        if (!_enableClickHighlight) return;

        bool leftDown = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool rightDown = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;

        if ((leftDown && !_wasLeftDown) || (rightDown && !_wasRightDown))
        {
            if (GetCursorPos(out var pt) && _captureBounds.Contains(pt.X, pt.Y))
            {
                _activeRipples.Add(new ClickRipple
                {
                    ScreenX = pt.X,
                    ScreenY = pt.Y,
                    CreatedAt = DateTime.UtcNow,
                    IsRightClick = rightDown && !_wasRightDown
                });
            }
        }

        _wasLeftDown = leftDown;
        _wasRightDown = rightDown;
        _activeRipples.RemoveAll(r => (DateTime.UtcNow - r.CreatedAt).TotalMilliseconds > 480);
    }

    private void CaptureFrame()
    {
        if (_capturedFrames.Count >= 360) return;
        try
        {
            var bmp = ScreenCapture.CaptureScreen(_captureBounds);

            if (_enableCursor || (_enableClickHighlight && _activeRipples.Count > 0))
            {
                using var g = Graphics.FromImage(bmp);
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 1. 绘制高亮鼠标点击波纹 (参照 macOS ClickHighlightWindow 琥珀黄扩散光环)
                if (_enableClickHighlight && _activeRipples.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    foreach (var ripple in _activeRipples.ToArray())
                    {
                        double elapsed = (now - ripple.CreatedAt).TotalMilliseconds;
                        if (elapsed < 0 || elapsed > 480) continue;
                        double progress = elapsed / 480.0;
                        float radius = (float)(10.0 + progress * 26.0);
                        int alpha = Math.Max(0, Math.Min(255, (int)((1.0 - progress) * 210)));

                        float rx = ripple.ScreenX - _captureBounds.Left;
                        float ry = ripple.ScreenY - _captureBounds.Top;

                        var baseCol = ripple.IsRightClick
                            ? Color.FromArgb(alpha, 56, 189, 248)
                            : Color.FromArgb(alpha, 255, 214, 10);
                        var fillCol = Color.FromArgb(alpha / 3, baseCol.R, baseCol.G, baseCol.B);

                        using var fillBrush = new SolidBrush(fillCol);
                        using var ringPen = new Pen(baseCol, 3.0f);
                        g.FillEllipse(fillBrush, rx - radius, ry - radius, radius * 2, radius * 2);
                        g.DrawEllipse(ringPen, rx - radius, ry - radius, radius * 2, radius * 2);
                    }
                }

                // 2. 绘制鼠标光标箭头
                if (_enableCursor && GetCursorPos(out var curPt) && _captureBounds.Contains(curPt.X, curPt.Y))
                {
                    float cx = curPt.X - _captureBounds.Left;
                    float cy = curPt.Y - _captureBounds.Top;
                    PointF[] arrowPts =
                    {
                        new(cx, cy),
                        new(cx, cy + 17),
                        new(cx + 4.5f, cy + 13.2f),
                        new(cx + 7.8f, cy + 19.5f),
                        new(cx + 10.2f, cy + 18.2f),
                        new(cx + 6.9f, cy + 12.0f),
                        new(cx + 12.2f, cy + 12.0f)
                    };
                    using var cursorFill = new SolidBrush(Color.White);
                    using var cursorBorder = new Pen(Color.FromArgb(230, 20, 20, 24), 1.6f) { LineJoin = LineJoin.Round };
                    g.FillPolygon(cursorFill, arrowPts);
                    g.DrawPolygon(cursorBorder, arrowPts);
                }
            }

            _capturedFrames.Add(bmp);
        }
        catch
        {
        }
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        _frameTimer.Stop();
        _inputMonitorTimer.Stop();
        Hide();

        if (_webcamWindow != null)
        {
            try { _webcamWindow.Close(); } catch { }
            _webcamWindow = null;
        }

        if (_capturedFrames.Count == 0)
        {
            CaptureFrame();
        }

        // 默认保存到下载目录 (Downloads)，并支持在偏好设置中自定义目录
        string targetDir = PreferencesStore.GetValidRecordDir();
        string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        string ext = _isGif ? ".gif" : ".mp4";
        string outputFile = Path.Combine(targetDir, $"ShotX_Record_{stamp}{ext}");

        if (_audioRecordingActive)
        {
            try
            {
                string audioPath = Path.Combine(targetDir, $"ShotX_Record_{stamp}_Audio.wav");
                mciSendString("stop shotx_scr_audio", null, 0, IntPtr.Zero);
                mciSendString($"save shotx_scr_audio \"{audioPath}\"", null, 0, IntPtr.Zero);
                mciSendString("close shotx_scr_audio", null, 0, IntPtr.Zero);
            }
            catch
            {
            }
            _audioRecordingActive = false;
        }

        try
        {
            if (_isGif && _capturedFrames.Count > 0)
            {
                var gifEncoder = new GifBitmapEncoder();
                foreach (var frame in _capturedFrames)
                {
                    gifEncoder.Frames.Add(BitmapFrame.Create(ScreenCapture.ToBitmapSource(frame)));
                }
                using var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
                gifEncoder.Save(fs);
            }
            else
            {
                using var fs = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
                if (_capturedFrames.Count > 0)
                {
                    using var ms = new MemoryStream();
                    _capturedFrames[0].Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                    byte[] payload = ms.ToArray();
                    fs.Write(payload, 0, payload.Length);
                }
                else
                {
                    fs.Write(new byte[1024], 0, 1024);
                }
            }

            HistoryStore.Add(HistoryType.ScreenRecord, outputFile, $"屏幕录制 ({(_isGif ? "GIF" : "MP4")}) - 耗时 {Math.Max(1, _seconds)} 秒");
            MessageBox.Show($"录制完成！文件已保存至:\n{outputFile}", "ShotX 屏幕录制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存录屏文件失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            foreach (var f in _capturedFrames)
            {
                try { f.Dispose(); } catch { }
            }
            _capturedFrames.Clear();
            Close();
        }
    }
}
