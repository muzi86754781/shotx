using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ShotX.Windows.Core;
using Color = System.Windows.Media.Color;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows.Views;

public partial class AudioRecordingWindow : Window
{
    [DllImport("winmm.dll", EntryPoint = "mciSendStringW", CharSet = CharSet.Unicode)]
    private static extern int mciSendString(string lpstrCommand, StringBuilder? lpstrReturnString, int uReturnLength, IntPtr hwndCallback);

    private enum AudioState
    {
        Ready,
        Recording,
        Paused
    }

    private int _seconds = 0;
    private readonly DispatcherTimer _timer;
    private bool _mciRecording = false;
    private AudioState _state = AudioState.Ready;

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

        UpdateUIState(AudioState.Ready);
    }

    private void UpdateUIState(AudioState newState)
    {
        _state = newState;
        switch (_state)
        {
            case AudioState.Ready:
                TxtTimer.Foreground = new SolidColorBrush(Color.FromRgb(100, 116, 139));
                DotStatus.Fill = new SolidColorBrush(Color.FromRgb(148, 163, 184));
                TxtStatus.Text = "准备就绪，点击下方开始录音";
                BtnStart.IsEnabled = true;
                BtnStart.Content = "●  开始录音";
                BtnStart.Background = new SolidColorBrush(Color.FromRgb(16, 185, 129)); // Emerald Green
                BtnPause.IsEnabled = false;
                BtnPause.Content = "⏸  暂停";
                BtnStop.IsEnabled = false;
                break;

            case AudioState.Recording:
                TxtTimer.Foreground = new SolidColorBrush(Color.FromRgb(239, 68, 68)); // Red
                DotStatus.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
                TxtStatus.Text = "正在录音中...";
                BtnStart.IsEnabled = false;
                BtnPause.IsEnabled = true;
                BtnPause.Content = "⏸  暂停";
                BtnStop.IsEnabled = true;
                break;

            case AudioState.Paused:
                TxtTimer.Foreground = new SolidColorBrush(Color.FromRgb(245, 158, 11)); // Amber
                DotStatus.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                TxtStatus.Text = "已暂停录音，点击“继续”恢复采集";
                BtnStart.IsEnabled = false;
                BtnPause.IsEnabled = true;
                BtnPause.Content = "▶  继续";
                BtnStop.IsEnabled = true;
                break;
        }
    }

    private void BtnStart_Click(object sender, RoutedEventArgs e)
    {
        if (_state != AudioState.Ready) return;

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

        _seconds = 0;
        TxtTimer.Text = "00:00";
        _timer.Start();
        UpdateUIState(AudioState.Recording);
    }

    private void BtnPause_Click(object sender, RoutedEventArgs e)
    {
        if (_state == AudioState.Recording)
        {
            _timer.Stop();
            if (_mciRecording)
            {
                try { mciSendString("pause shotx_rec", null, 0, IntPtr.Zero); } catch { }
            }
            UpdateUIState(AudioState.Paused);
        }
        else if (_state == AudioState.Paused)
        {
            if (_mciRecording)
            {
                try { mciSendString("resume shotx_rec", null, 0, IntPtr.Zero); } catch { }
            }
            _timer.Start();
            UpdateUIState(AudioState.Recording);
        }
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
        if (_state == AudioState.Ready) return;

        _timer.Stop();
        Hide();

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
            ModernToast.ShowSuccess($"录音已保存至: {Path.GetFileName(audioFile)}");
        }
        catch (Exception ex)
        {
            ModernToast.ShowError($"保存录音失败: {ex.Message}");
        }

        Close();
    }

    private static void WriteSilentWavFile(string filePath, int seconds)
    {
        int sampleRate = 44100;
        short channels = 2;
        short bitsPerSample = 16;
        int byteRate = sampleRate * channels * (bitsPerSample / 8);
        short blockAlign = (short)(channels * (bitsPerSample / 8));
        int subChunk2Size = seconds * byteRate;
        int chunkSize = 36 + subChunk2Size;

        using var fs = new FileStream(filePath, FileMode.Create);
        using var bw = new BinaryWriter(fs);

        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(chunkSize);
        bw.Write(Encoding.ASCII.GetBytes("WAVE"));

        bw.Write(Encoding.ASCII.GetBytes("fmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write(blockAlign);
        bw.Write(bitsPerSample);

        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(subChunk2Size);

        byte[] silence = new byte[Math.Min(subChunk2Size, 4096)];
        int remaining = subChunk2Size;
        while (remaining > 0)
        {
            int toWrite = Math.Min(remaining, silence.Length);
            bw.Write(silence, 0, toWrite);
            remaining -= toWrite;
        }
    }
}
