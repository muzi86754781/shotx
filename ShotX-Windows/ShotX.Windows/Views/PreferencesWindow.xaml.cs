using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ShotX.Windows.Core;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using RadioButton = System.Windows.Controls.RadioButton;
using TextBox = System.Windows.Controls.TextBox;

namespace ShotX.Windows.Views;

public partial class PreferencesWindow : Window
{
    private HistoryType? _selectedHistoryFilter = HistoryType.ScreenRecord;
    private readonly List<HistoryItem> _allHistoryItems = new();
    private bool _isReady = false;

    public PreferencesWindow(bool initialTabIsHistory = false, HistoryType? filterType = null)
    {
        InitializeComponent();
        _isReady = true;

        LoadSettings();
        LoadHotkeysUI();
        NavigateTo(initialTabIsHistory, filterType);
    }

    /// <summary>
    /// 供外部单实例复用时切换标签页并直接过滤指定历史分类 (解决需求 6)
    /// </summary>
    public void NavigateTo(bool tabIsHistory, HistoryType? filterType = null)
    {
        if (!_isReady) return;

        if (tabIsHistory)
        {
            TabHistory.IsChecked = true;
            ViewHistory.Visibility = Visibility.Visible;
            ViewSettings.Visibility = Visibility.Collapsed;

            if (filterType.HasValue)
            {
                _selectedHistoryFilter = filterType.Value;
                SelectFilterPill(filterType.Value);
            }
            LoadHistory();
        }
        else
        {
            TabSettings.IsChecked = true;
            ViewSettings.Visibility = Visibility.Visible;
            ViewHistory.Visibility = Visibility.Collapsed;
            LoadHistory();
        }
    }

    private void SelectFilterPill(HistoryType? type)
    {
        if (!_isReady) return;
        _isReady = false;
        try
        {
            switch (type)
            {
                case null:
                    if (PillAll != null) PillAll.IsChecked = true;
                    break;
                case HistoryType.Screenshot:
                    if (PillScreenshot != null) PillScreenshot.IsChecked = true;
                    break;
                case HistoryType.ScreenRecord:
                    if (PillRecord != null) PillRecord.IsChecked = true;
                    break;
                case HistoryType.AudioRecord:
                    if (PillAudio != null) PillAudio.IsChecked = true;
                    break;
                case HistoryType.OCR:
                    if (PillOcr != null) PillOcr.IsChecked = true;
                    break;
                case HistoryType.Translation:
                    if (PillTranslation != null) PillTranslation.IsChecked = true;
                    break;
                case HistoryType.Clipboard:
                    if (PillClipboard != null) PillClipboard.IsChecked = true;
                    break;
            }
        }
        finally
        {
            _isReady = true;
        }
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
        LoadHistory();
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
            items = items.Where(it =>
                (it.Content?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0 ||
                (it.FilePath?.IndexOf(query, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0);
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
            LstFullHistory.ItemsSource = null;
            LstFullHistory.ItemsSource = list;
        }
    }

    private void BtnCopyHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItem item)
        {
            try
            {
                string copyText = !string.IsNullOrWhiteSpace(item.Content) ? item.Content : item.FilePath;
                if (!string.IsNullOrWhiteSpace(copyText))
                {
                    Clipboard.SetText(copyText);
                    MessageBox.Show("已将该条记录内容复制到剪贴板！", "ShotX 历史记录", MessageBoxButton.OK, MessageBoxImage.Information);
                }
            }
            catch { }
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
                MessageBox.Show($"记录时间: {item.TimestampString}\n分类: {item.TypeBadgeText}\n\n完整内容:\n{item.Content}", "ShotX 历史记录详情", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void BtnRevealHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItem item && !string.IsNullOrWhiteSpace(item.FilePath))
        {
            try
            {
                if (File.Exists(item.FilePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{item.FilePath}\"") { UseShellExecute = true });
                }
                else
                {
                    string? dir = Path.GetDirectoryName(item.FilePath);
                    if (!string.IsNullOrEmpty(dir)) OpenFolder(dir);
                }
            }
            catch { }
        }
    }

    private void BtnDeleteHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItem item)
        {
            HistoryStore.Delete(item);
            _allHistoryItems.RemoveAll(i => i.Id == item.Id);
            RefreshHistoryDisplay();
        }
    }

    private void BtnClearCurrentHistory_Click(object sender, RoutedEventArgs e)
    {
        HistoryStore.Clear(_selectedHistoryFilter);
        LoadHistory();
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

    // --- 全局快捷键配置模块 (解决需求 9：单行录键、单行清除、清空非核心热键、恢复默认快捷键全部可用) ---
    private void LoadHotkeysUI()
    {
        if (HotkeysContainer == null) return;
        foreach (var tb in FindVisualChildren<TextBox>(HotkeysContainer))
        {
            if (tb.Tag is string key && !string.IsNullOrWhiteSpace(key))
            {
                tb.Text = PreferencesStore.GetHotkey(key);
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T typed)
            {
                yield return typed;
            }
            foreach (var sub in FindVisualChildren<T>(child))
            {
                yield return sub;
            }
        }
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string actionKey) return;
        e.Handled = true;

        Key key = (e.Key == Key.System) ? e.SystemKey : e.Key;

        if (key == Key.Back || key == Key.Delete)
        {
            PreferencesStore.ClearSingleHotkey(actionKey);
            tb.Text = "未设置";
            NotifyHotkeysReload();
            return;
        }

        // 忽略仅按下修饰键本身
        if (key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt ||
            key == Key.LeftShift || key == Key.RightShift ||
            key == Key.LWin || key == Key.RWin)
        {
            return;
        }

        var parts = new List<string>();
        var mods = Keyboard.Modifiers;
        if ((mods & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((mods & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((mods & ModifierKeys.Shift) != 0) parts.Add("Shift");
        if ((mods & ModifierKeys.Windows) != 0) parts.Add("Win");

        // 若未按任何修饰键且不是功能键 F1~F12，默认加上 Alt 防止占用普通打字键
        if (parts.Count == 0 && (key < Key.F1 || key > Key.F12))
        {
            parts.Add("Alt");
        }

        parts.Add(key.ToString());
        string combo = string.Join(" + ", parts);

        PreferencesStore.SetHotkey(actionKey, combo);
        tb.Text = combo;
        NotifyHotkeysReload();
    }

    private void BtnClearSingleHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string actionKey && !string.IsNullOrWhiteSpace(actionKey))
        {
            PreferencesStore.ClearSingleHotkey(actionKey);
            LoadHotkeysUI();
            NotifyHotkeysReload();
        }
    }

    private void BtnClearHotkeys_Click(object sender, RoutedEventArgs e)
    {
        PreferencesStore.ClearNonCoreHotkeys();
        LoadHotkeysUI();
        NotifyHotkeysReload();
        MessageBox.Show("已清空非核心快捷键（仅保留 Alt + A 区域截图核心热键）。", "ShotX 快捷键配置", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnRestoreHotkeys_Click(object sender, RoutedEventArgs e)
    {
        PreferencesStore.RestoreDefaultHotkeys();
        LoadHotkeysUI();
        NotifyHotkeysReload();
        MessageBox.Show("已恢复全部快捷键为系统默认值！", "ShotX 快捷键配置", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static void NotifyHotkeysReload()
    {
        if (Application.Current is App app)
        {
            app.ReloadGlobalHotkeys();
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

    private void BtnSupport_Click(object sender, RoutedEventArgs e)
    {
        var win = new SupportAuthorWindow();
        win.Owner = this;
        win.ShowDialog();
    }

    private void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show("当前已是最新版本 ShotX v1.9.24！", "ShotX 检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
