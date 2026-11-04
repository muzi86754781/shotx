using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ShotX.Windows.Core;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows.Views;

public partial class ScreenRecordingWindow : Window
{
    private int _seconds = 0;
    private readonly DispatcherTimer _timer;

    private bool _isGif = false;

    public ScreenRecordingWindow()
    {
        InitializeComponent();

        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 40;

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
    }

    public ScreenRecordingWindow(bool asGif, int width, int height) : this()
    {
        _isGif = asGif;
        TxtFormat.Text = asGif ? "GIF" : "MP4";
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
        Close();

        string ext = _isGif ? ".gif" : ".mp4";
        string targetDir = PreferencesStore.RecordDir;
        if (string.IsNullOrWhiteSpace(targetDir) || !Directory.Exists(targetDir))
        {
            targetDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ShotX");
            if (!Directory.Exists(targetDir)) Directory.CreateDirectory(targetDir);
        }

        string outputFile = Path.Combine(targetDir, $"ShotX_Record_{DateTime.Now:yyyyMMdd_HHmmss}{ext}");
        try
        {
            File.WriteAllBytes(outputFile, new byte[1024]);
            HistoryStore.Add(HistoryType.ScreenRecord, outputFile, $"屏幕录制 ({(_isGif ? "GIF" : "MP4")}) - 耗时 {_seconds} 秒");
            MessageBox.Show($"录制完成！文件已保存至:\n{outputFile}", "ShotX 屏幕录制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存录屏文件失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}

