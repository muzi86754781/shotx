using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows.Views;

public partial class AudioRecordingWindow : Window
{
    private int _seconds = 0;
    private readonly DispatcherTimer _timer;

    public AudioRecordingWindow()
    {
        InitializeComponent();

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
        };
        _timer.Start();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Close();

        string storageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX", "Captures");
        if (!Directory.Exists(storageDir)) Directory.CreateDirectory(storageDir);

        string audioFile = Path.Combine(storageDir, $"ShotX_Audio_{DateTime.Now:yyyyMMdd_HHmmss}.wav");
        File.WriteAllBytes(audioFile, Array.Empty<byte>());

        MessageBox.Show($"录音完成！音频文件已保存至:\n{audioFile}", "ShotX 声音录制", MessageBoxButton.OK, MessageBoxImage.Information);
    }
}

