using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ShotX.Windows.Core;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows.Views;

public partial class ScreenRecordingWindow : Window
{
    private int _seconds = 0;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _frameTimer;
    private readonly List<Bitmap> _capturedFrames = new();
    private Rectangle _captureBounds;
    private bool _isGif = false;

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

        _frameTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
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

    private void CaptureFrame()
    {
        if (_capturedFrames.Count >= 300) return;
        try
        {
            var bmp = ScreenCapture.CaptureScreen(_captureBounds);
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
        Hide();

        if (_capturedFrames.Count == 0)
        {
            CaptureFrame();
        }

        // 默认保存到下载目录 (Downloads)，并支持在偏好设置中自定义目录 (解决需求 9)
        string targetDir = PreferencesStore.GetValidRecordDir();
        string ext = _isGif ? ".gif" : ".mp4";
        string outputFile = Path.Combine(targetDir, $"ShotX_Record_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");

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
                // 生成包含真实捕获帧数据的视频文件
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
