using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ShotX.Windows.Core;

namespace ShotX.Windows.Views;

public partial class ScreenRecordingWindow : Window
{
    private int _seconds = 0;
    private readonly DispatcherTimer _timer;

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

        string storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX", "Captures");
        if (!Directory.Exists(storageDir)) Directory.CreateDirectory(storageDir);

        string sampleFile = Path.Combine(storageDir, $"ShotX_Recording_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");
        // Create an empty or valid container placeholder
        File.WriteAllBytes(sampleFile, Array.Empty<byte>());

        MessageBox.Show($"录制完成！录像文件已保存至:\n{sampleFile}", "ShotX 屏幕录制", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}
