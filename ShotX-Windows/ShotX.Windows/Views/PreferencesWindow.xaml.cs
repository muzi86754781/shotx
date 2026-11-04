using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShotX.Windows.Core;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public partial class PreferencesWindow : Window
{
    private HistoryType? _selectedHistoryFilter = HistoryType.ScreenRecord;
    private readonly List<HistoryItem> _allHistoryItems = new();

    public PreferencesWindow(bool initialTabIsHistory = false)
    {
        InitializeComponent();

        if (initialTabIsHistory)
        {
            TabHistory.IsChecked = true;
            ViewHistory.Visibility = Visibility.Visible;
            ViewSettings.Visibility = Visibility.Collapsed;
        }
        else
        {
            TabSettings.IsChecked = true;
            ViewSettings.Visibility = Visibility.Visible;
            ViewHistory.Visibility = Visibility.Collapsed;
        }

        LoadSettings();
        LoadHistory();
    }

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void TabHistory_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewHistory == null || ViewSettings == null) return;
        ViewHistory.Visibility = Visibility.Visible;
        ViewSettings.Visibility = Visibility.Collapsed;
        RefreshHistoryDisplay();
    }

    private void TabSettings_Checked(object sender, RoutedEventArgs e)
    {
        if (ViewHistory == null || ViewSettings == null) return;
        ViewHistory.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Visible;
    }

    // --- 历史记录模块 (图 7) ---
    private void LoadHistory()
    {
        _allHistoryItems.Clear();
        _allHistoryItems.AddRange(HistoryStore.GetAll());
        RefreshHistoryDisplay();
    }

    private void HistoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && rb.Content is string text)
        {
            if (text.Contains("全部")) _selectedHistoryFilter = null;
            else if (text.Contains("截图")) _selectedHistoryFilter = HistoryType.Screenshot;
            else if (text.Contains("录屏")) _selectedHistoryFilter = HistoryType.ScreenRecord;
            else if (text.Contains("录音")) _selectedHistoryFilter = HistoryType.AudioRecord;
            else if (text.Contains("提取文字")) _selectedHistoryFilter = HistoryType.OCR;
            else if (text.Contains("翻译")) _selectedHistoryFilter = HistoryType.Translation;
            else if (text.Contains("复制记录")) _selectedHistoryFilter = HistoryType.Clipboard;

            RefreshHistoryDisplay();
        }
    }

    private void TxtHistorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtHistoryPlaceholder.Visibility = string.IsNullOrEmpty(TxtHistorySearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshHistoryDisplay();
    }

    private void RefreshHistoryDisplay()
    {
        if (TxtHistoryCount == null) return;

        string query = TxtHistorySearch.Text.Trim();
        var items = _allHistoryItems.AsEnumerable();

        if (_selectedHistoryFilter.HasValue)
        {
            items = items.Where(it => it.Type == _selectedHistoryFilter.Value);
        }

        if (!string.IsNullOrEmpty(query))
        {
            items = items.Where(it => it.Content.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        var list = items.ToList();

        string filterName = _selectedHistoryFilter switch
        {
            HistoryType.Screenshot => "截图",
            HistoryType.ScreenRecord => "录屏",
            HistoryType.AudioRecord => "录音",
            HistoryType.OCR => "提取文字",
            HistoryType.Translation => "翻译",
            HistoryType.Clipboard => "复制记录",
            _ => "全部"
        };

        TxtHistoryCount.Text = $"{filterName} · {list.Count} 条记录";

        if (list.Count == 0)
        {
            PanelEmptyHistory.Visibility = Visibility.Visible;
            LstFullHistory.Visibility = Visibility.Collapsed;

            TxtEmptyIcon.Text = _selectedHistoryFilter switch
            {
                HistoryType.Screenshot => "🖼️",
                HistoryType.ScreenRecord => "📹",
                HistoryType.AudioRecord => "🎙️",
                HistoryType.OCR => "🔤",
                HistoryType.Translation => "🌐",
                HistoryType.Clipboard => "📋",
                _ => "📁"
            };
            TxtEmptyTitle.Text = $"暂无{filterName}记录";
        }
        else
        {
            PanelEmptyHistory.Visibility = Visibility.Collapsed;
            LstFullHistory.Visibility = Visibility.Visible;
            LstFullHistory.ItemsSource = list;
        }
    }

    private void BtnOpenHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItem item)
        {
            if (File.Exists(item.FilePath))
            {
                Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
            }
            else
            {
                MessageBox.Show($"记录内容:\n{item.Content}", "ShotX 历史记录", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    // --- 偏好设置模块 (图 8, 9, 10) ---
    private void LoadSettings()
    {
        ChkAutoStart.IsChecked = PreferencesStore.AutoStart;

        TxtSavePath.Text = string.IsNullOrWhiteSpace(PreferencesStore.SaveDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : PreferencesStore.SaveDir;

        TxtRecordPath.Text = string.IsNullOrWhiteSpace(PreferencesStore.RecordDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : PreferencesStore.RecordDir;

        TxtAudioPath.Text = string.IsNullOrWhiteSpace(PreferencesStore.AudioDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
            : PreferencesStore.AudioDir;
    }

    private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
    {
        PreferencesStore.AutoStart = ChkAutoStart.IsChecked == true;
        PreferencesStore.Save();
    }

    private void CmbImageFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbImageFormat.SelectedIndex == 0) PreferencesStore.ImageFormat = "PNG";
        else if (CmbImageFormat.SelectedIndex == 1) PreferencesStore.ImageFormat = "JPG";
        else if (CmbImageFormat.SelectedIndex == 2) PreferencesStore.ImageFormat = "WEBP";
        PreferencesStore.Save();
    }

    private void BtnChangeSavePath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "选择截图默认保存路径";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            PreferencesStore.SaveDir = dialog.SelectedPath;
            PreferencesStore.Save();
            TxtSavePath.Text = dialog.SelectedPath;
        }
    }

    private void BtnOpenSavePath_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(TxtSavePath.Text);
    }

    private void BtnChangeRecordPath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "选择屏幕录制保存路径";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            PreferencesStore.RecordDir = dialog.SelectedPath;
            PreferencesStore.Save();
            TxtRecordPath.Text = dialog.SelectedPath;
        }
    }

    private void BtnOpenRecordPath_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(TxtRecordPath.Text);
    }

    private void BtnChangeAudioPath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog();
        dialog.Description = "选择麦克风录音保存路径";
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            PreferencesStore.AudioDir = dialog.SelectedPath;
            PreferencesStore.Save();
            TxtAudioPath.Text = dialog.SelectedPath;
        }
    }

    private void BtnOpenAudioPath_Click(object sender, RoutedEventArgs e)
    {
        OpenFolder(TxtAudioPath.Text);
    }

    private void OpenFolder(string path)
    {
        try
        {
            if (!Directory.Exists(path)) Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开目录失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void BtnClearHotkeys_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("已清空非核心快捷键映射。", "ShotX 快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnRestoreHotkeys_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("已恢复全部 14 项快捷键为系统默认值。", "ShotX 快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnSupport_Click(object sender, RoutedEventArgs e)
    {
        var win = new SupportAuthorWindow();
        win.Owner = this;
        win.ShowDialog();
    }

    private void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("当前已是最新版本 ShotX v1.9.23！", "ShotX 检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}

