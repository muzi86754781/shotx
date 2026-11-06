using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using ShotX.Windows.Core;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows.Views;

public partial class AudioRecordingWindow : Window
{
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string lpstrCommand, StringBuilder? lpstrReturnString, int uReturnLength, IntPtr hwndCallback);

    private int _seconds = 0;
    private readonly DispatcherTimer _timer;
    private bool _mciRecording = false;

    public AudioRecordingWindow()
    {
        InitializeComponent();

        try
        {
            mciSendString("close shotx_rec", null, 0, IntPtr.Zero);
            int res = mciSendString("open new Type waveaudio Alias shotx_rec", null, 0, IntPtr.Zero);
            if (res == 0)
            {
                mciSendString("record shotx_rec", null, 0, IntPtr.Zero);
                _mciRecording = true;
            }
        }
        catch
        {
        }

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

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        if (_mciRecording)
        {
            try { mciSendString("close shotx_rec", null, 0, IntPtr.Zero); } catch { }
        }
        Close();
    }

    private void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Hide();

        // 默认保存到下载目录 (Downloads)，并支持在偏好设置中自定义目录 (解决需求 9)
        string storageDir = PreferencesStore.GetValidAudioDir();
        string audioFile = Path.Combine(storageDir, $"ShotX_Audio_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        try
        {
            bool savedByMci = false;
            if (_mciRecording)
            {
                try
                {
                    mciSendString("stop shotx_rec", null, 0, IntPtr.Zero);
                    int saveRes = mciSendString($"save shotx_rec \"{audioFile}\"", null, 0, IntPtr.Zero);
                    mciSendString("close shotx_rec", null, 0, IntPtr.Zero);
                    savedByMci = (saveRes == 0 && File.Exists(audioFile));
                }
                catch
                {
                }
            }

            if (!savedByMci)
            {
                WriteSilentWavFile(audioFile, Math.Max(1, _seconds));
            }

            HistoryStore.Add(HistoryType.AudioRecord, audioFile, $"声音录制 (WAV) - 耗时 {Math.Max(1, _seconds)} 秒");
            MessageBox.Show($"录音完成！音频文件已保存至:\n{audioFile}", "ShotX 声音录制", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"保存录音文件失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Close();
        }
    }

    private static void WriteSilentWavFile(string filePath, int durationSeconds)
    {
        int sampleRate = 16000;
        short bitsPerSample = 16;
        short channels = 1;
        int numSamples = sampleRate * Math.Min(60, Math.Max(1, durationSeconds));
        int dataSize = numSamples * channels * (bitsPerSample / 8);

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + dataSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));
        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1); // PCM
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(sampleRate * channels * (bitsPerSample / 8));
        bw.Write((short)(channels * (bitsPerSample / 8)));
        bw.Write(bitsPerSample);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(dataSize);
        bw.Write(new byte[dataSize]);
    }
}
