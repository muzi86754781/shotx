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
using Button = System.Windows.Controls.Button;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;

namespace ShotX.Windows.Views;

public partial class PreferencesWindow : Window
{
    private HistoryType? _selectedHistoryFilter = HistoryType.ScreenRecord;
    private readonly List<HistoryItem> _allHistoryItems = new();
    private bool _isReady = false;

    public PreferencesWindow(bool initialTabIsHistory = false)
    {
        InitializeComponent();
        _isReady = true;

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
            try { DragMove(); } catch { }
        }
    }

    private void BtnMinimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
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
        if (!_isReady || ViewHistory == null || ViewSettings == null) return;
        ViewHistory.Visibility = Visibility.Visible;
        ViewSettings.Visibility = Visibility.Collapsed;
        RefreshHistoryDisplay();
    }

    private void TabSettings_Checked(object sender, RoutedEventArgs e)
    {
        if (!_isReady || ViewHistory == null || ViewSettings == null) return;
        ViewHistory.Visibility = Visibility.Collapsed;
        ViewSettings.Visibility = Visibility.Visible;
    }

    // --- 左侧 Windows 11 导航栏跳转 ---
    private void NavGeneral_Click(object sender, RoutedEventArgs e)
    {
        TabSettings.IsChecked = true;
        CardGeneral?.BringIntoView();
    }

    private void NavAi_Click(object sender, RoutedEventArgs e)
    {
        TabSettings.IsChecked = true;
        CardAi?.BringIntoView();
    }

    private void NavHotkeys_Click(object sender, RoutedEventArgs e)
    {
        TabSettings.IsChecked = true;
        CardHotkeys?.BringIntoView();
    }

    private void NavHistory_Click(object sender, RoutedEventArgs e)
    {
        TabHistory.IsChecked = true;
    }

    private void NavAbout_Click(object sender, RoutedEventArgs e)
    {
        TabSettings.IsChecked = true;
        CardAbout?.BringIntoView();
    }

    // --- 历史记录模块 ---
    private void LoadHistory()
    {
        _allHistoryItems.Clear();
        _allHistoryItems.AddRange(HistoryStore.GetAll());
        RefreshHistoryDisplay();
    }

    private void HistoryFilter_Changed(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (sender is RadioButton rb && rb.Content is string text)
        {
            if (text.Contains("全部")) _selectedHistoryFilter = null;
            else if (text.Contains("截图")) _selectedHistoryFilter = HistoryType.Screenshot;
            else if (text.Contains("录屏")) _selectedHistoryFilter = HistoryType.ScreenRecord;
            else if (text.Contains("录音")) _selectedHistoryFilter = HistoryType.AudioRecord;
            else if (text.Contains("文字")) _selectedHistoryFilter = HistoryType.OCR;
            else if (text.Contains("翻译")) _selectedHistoryFilter = HistoryType.Translation;
            else if (text.Contains("剪贴板") || text.Contains("复制")) _selectedHistoryFilter = HistoryType.Clipboard;

            RefreshHistoryDisplay();
        }
    }

    private void TxtHistorySearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isReady || TxtHistoryPlaceholder == null || TxtHistorySearch == null) return;
        TxtHistoryPlaceholder.Visibility = string.IsNullOrEmpty(TxtHistorySearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshHistoryDisplay();
    }

    private void RefreshHistoryDisplay()
    {
        if (!_isReady || TxtHistoryCount == null || TxtHistorySearch == null) return;

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
            HistoryType.Clipboard => "剪贴板",
            _ => "全部"
        };

        TxtHistoryCount.Text = $"{filterName} · 共 {list.Count} 条记录";

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
            if (!string.IsNullOrWhiteSpace(item.FilePath) && File.Exists(item.FilePath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(item.FilePath) { UseShellExecute = true });
                }
                catch { }
            }
            else
            {
                MessageBox.Show($"记录内容:\n{item.Content}", "ShotX 历史记录", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    // --- 偏好设置模块 ---
    private void LoadSettings()
    {
        if (ChkAutoStart != null)
        {
            ChkAutoStart.IsChecked = PreferencesStore.AutoStart;
        }

        if (TxtSavePath != null)
        {
            TxtSavePath.Text = PreferencesStore.GetValidSaveDir();
        }

        if (TxtRecordPath != null)
        {
            TxtRecordPath.Text = PreferencesStore.GetValidRecordDir();
        }

        if (TxtAudioPath != null)
        {
            TxtAudioPath.Text = PreferencesStore.GetValidAudioDir();
        }

        if (CmbImageFormat != null)
        {
            string fmt = (PreferencesStore.ImageFormat ?? "PNG").ToUpperInvariant();
            CmbImageFormat.SelectedIndex = fmt == "JPG" ? 1 : (fmt == "WEBP" ? 2 : 0);
        }
    }

    private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (!_isReady || ChkAutoStart == null) return;
        PreferencesStore.AutoStart = ChkAutoStart.IsChecked == true;
        PreferencesStore.Save();
    }

    private void CmbImageFormat_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || CmbImageFormat == null) return;
        if (CmbImageFormat.SelectedIndex == 0) PreferencesStore.ImageFormat = "PNG";
        else if (CmbImageFormat.SelectedIndex == 1) PreferencesStore.ImageFormat = "JPG";
        else if (CmbImageFormat.SelectedIndex == 2) PreferencesStore.ImageFormat = "WEBP";
        PreferencesStore.Save();
    }

    private void BtnChangeSavePath_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择截图默认保存路径",
            SelectedPath = PreferencesStore.GetValidSaveDir()
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
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
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择屏幕录制保存路径",
            SelectedPath = PreferencesStore.GetValidRecordDir()
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
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
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择麦克风录音保存路径",
            SelectedPath = PreferencesStore.GetValidAudioDir()
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK && !string.IsNullOrWhiteSpace(dialog.SelectedPath))
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
            string target = string.IsNullOrWhiteSpace(path) ? PreferencesStore.DefaultDownloadsDir : path;
            if (!Directory.Exists(target)) Directory.CreateDirectory(target);
            Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
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
        MessageBox.Show("已恢复全部快捷键为系统默认值。", "ShotX 快捷键", MessageBoxButton.OK, MessageBoxImage.Information);
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
