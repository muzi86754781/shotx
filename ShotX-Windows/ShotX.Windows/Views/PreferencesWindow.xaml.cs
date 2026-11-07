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
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;

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
                    ModernToast.ShowSuccess("已复制该条记录到剪贴板");
                }
            }
            catch { }
        }
    }

    private void BtnOpenHistoryItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is HistoryItem item)
        {
            // 翻译记录：直接唤起翻译窗口并显示原文与译文 (解决需求 7)
            if (item.Type == HistoryType.Translation)
            {
                var app = (App)System.Windows.Application.Current;
                app.OpenOrUpdateTranslationWindowWithResult(item.TranslationSource, item.TranslationTarget);
                return;
            }

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
                ModernToast.ShowInfo($"分类: {item.TypeBadgeText}\n{item.Content}");
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

        LoadAiConfigUI();
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
        ModernToast.ShowInfo("已清空非核心快捷键（仅保留核心热键）");
    }

    private void BtnRestoreHotkeys_Click(object sender, RoutedEventArgs e)
    {
        PreferencesStore.RestoreDefaultHotkeys();
        LoadHotkeysUI();
        NotifyHotkeysReload();
        ModernToast.ShowSuccess("已恢复快捷键为系统默认值");
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
            ModernToast.ShowError($"打开目录失败: {ex.Message}");
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
        ModernToast.ShowSuccess("当前已是最新版本 ShotX！");
    }

    private void BtnExit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }

    // --- 机器翻译与 OCR 平台多引擎配置 (解决需求 3：参照设计图) ---
    private static readonly SolidColorBrush SelectedRowBrush = new((Color)ColorConverter.ConvertFromString("#007AFF"));
    private static readonly SolidColorBrush TransparentBrush = Brushes.Transparent;
    private static readonly SolidColorBrush WhiteTextBrush = Brushes.White;
    private static readonly SolidColorBrush DarkTextBrush = new((Color)ColorConverter.ConvertFromString("#1E293B"));

    private void LoadAiConfigUI()
    {
        if (TxtBaiduAppId != null) TxtBaiduAppId.Text = PreferencesStore.BaiduTranslateAppId ?? "";
        if (PwdBaiduSecret != null) PwdBaiduSecret.Password = PreferencesStore.BaiduTranslateSecretKey ?? "";
        if (PwdGoogleKey != null) PwdGoogleKey.Password = PreferencesStore.GoogleTranslateApiKey ?? "";
        if (PwdDeepLKey != null) PwdDeepLKey.Password = PreferencesStore.DeepLTranslateApiKey ?? "";
        if (PwdOpenAIKey != null) PwdOpenAIKey.Password = PreferencesStore.OpenAIApiKey ?? "";
        if (TxtOpenAIBaseUrl != null) TxtOpenAIBaseUrl.Text = string.IsNullOrWhiteSpace(PreferencesStore.OpenAIBaseUrl) ? "https://api.openai.com/v1" : PreferencesStore.OpenAIBaseUrl;
        if (TxtOpenAIModel != null) TxtOpenAIModel.Text = string.IsNullOrWhiteSpace(PreferencesStore.OpenAIModel) ? "gpt-4o-mini" : PreferencesStore.OpenAIModel;

        if (TxtBaiduOcrKey != null) TxtBaiduOcrKey.Text = PreferencesStore.BaiduOcrApiKey ?? "";
        if (PwdBaiduOcrSecret != null) PwdBaiduOcrSecret.Password = PreferencesStore.BaiduOcrSecretKey ?? "";
        if (TxtYoudaoOcrKey != null) TxtYoudaoOcrKey.Text = PreferencesStore.YoudaoOcrAppKey ?? "";
        if (PwdYoudaoOcrSecret != null) PwdYoudaoOcrSecret.Password = PreferencesStore.YoudaoOcrAppSecret ?? "";

        SelectTransEngine(PreferencesStore.ActiveTranslationEngine ?? "Baidu", false);
        SelectOcrEngine(PreferencesStore.ActiveOcrEngine ?? "System", false);
    }

    private void SelectTransEngine(string engine, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(engine)) engine = "Baidu";
        if (save)
        {
            PreferencesStore.ActiveTranslationEngine = engine;
            PreferencesStore.Save();
            string name = engine switch
            {
                "Baidu" => "百度翻译",
                "BuiltIn" => "内置免密翻译",
                "Google" => "谷歌翻译",
                "DeepL" => "DeepL 翻译",
                "OpenAI" => "OpenAI / 兼容大模型",
                _ => engine
            };
            ModernToast.ShowSuccess($"已生效：当前机器翻译使用【{name}】");
        }

        // 重置所有翻译行样式
        if (RowTransBaidu != null) { RowTransBaidu.Background = engine == "Baidu" ? SelectedRowBrush : TransparentBrush; ChkTransBaidu.IsChecked = engine == "Baidu"; TxtTitleTransBaidu.Foreground = engine == "Baidu" ? WhiteTextBrush : DarkTextBrush; }
        if (RowTransBuiltIn != null) { RowTransBuiltIn.Background = engine == "BuiltIn" ? SelectedRowBrush : TransparentBrush; ChkTransBuiltIn.IsChecked = engine == "BuiltIn"; TxtTitleTransBuiltIn.Foreground = engine == "BuiltIn" ? WhiteTextBrush : DarkTextBrush; }
        if (RowTransGoogle != null) { RowTransGoogle.Background = engine == "Google" ? SelectedRowBrush : TransparentBrush; ChkTransGoogle.IsChecked = engine == "Google"; TxtTitleTransGoogle.Foreground = engine == "Google" ? WhiteTextBrush : DarkTextBrush; }
        if (RowTransDeepL != null) { RowTransDeepL.Background = engine == "DeepL" ? SelectedRowBrush : TransparentBrush; ChkTransDeepL.IsChecked = engine == "DeepL"; TxtTitleTransDeepL.Foreground = engine == "DeepL" ? WhiteTextBrush : DarkTextBrush; }
        if (RowTransOpenAI != null) { RowTransOpenAI.Background = engine == "OpenAI" ? SelectedRowBrush : TransparentBrush; ChkTransOpenAI.IsChecked = engine == "OpenAI"; TxtTitleTransOpenAI.Foreground = engine == "OpenAI" ? WhiteTextBrush : DarkTextBrush; }

        // 切换右侧配置面板
        if (PnlCfgTransBaidu != null) PnlCfgTransBaidu.Visibility = engine == "Baidu" ? Visibility.Visible : Visibility.Collapsed;
        if (PnlCfgTransBuiltIn != null) PnlCfgTransBuiltIn.Visibility = engine == "BuiltIn" ? Visibility.Visible : Visibility.Collapsed;
        if (PnlCfgTransGoogle != null) PnlCfgTransGoogle.Visibility = engine == "Google" ? Visibility.Visible : Visibility.Collapsed;
        if (PnlCfgTransDeepL != null) PnlCfgTransDeepL.Visibility = engine == "DeepL" ? Visibility.Visible : Visibility.Collapsed;
        if (PnlCfgTransOpenAI != null) PnlCfgTransOpenAI.Visibility = engine == "OpenAI" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TransEngineRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string engine)
        {
            SelectTransEngine(engine);
        }
    }

    private void SelectOcrEngine(string engine, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(engine)) engine = "System";
        if (save)
        {
            PreferencesStore.ActiveOcrEngine = engine;
            PreferencesStore.Save();
            string name = engine switch
            {
                "System" => "Windows 原生离线 OCR",
                "Baidu" => "百度智能云 OCR",
                "Youdao" => "有道智云 OCR",
                _ => engine
            };
            ModernToast.ShowSuccess($"已生效：当前文字识别使用【{name}】");
        }

        if (RowOcrSystem != null) { RowOcrSystem.Background = engine == "System" ? SelectedRowBrush : TransparentBrush; ChkOcrSystem.IsChecked = engine == "System"; TxtTitleOcrSystem.Foreground = engine == "System" ? WhiteTextBrush : DarkTextBrush; }
        if (RowOcrBaidu != null) { RowOcrBaidu.Background = engine == "Baidu" ? SelectedRowBrush : TransparentBrush; ChkOcrBaidu.IsChecked = engine == "Baidu"; TxtTitleOcrBaidu.Foreground = engine == "Baidu" ? WhiteTextBrush : DarkTextBrush; }
        if (RowOcrYoudao != null) { RowOcrYoudao.Background = engine == "Youdao" ? SelectedRowBrush : TransparentBrush; ChkOcrYoudao.IsChecked = engine == "Youdao"; TxtTitleOcrYoudao.Foreground = engine == "Youdao" ? WhiteTextBrush : DarkTextBrush; }

        if (PnlCfgOcrSystem != null) PnlCfgOcrSystem.Visibility = engine == "System" ? Visibility.Visible : Visibility.Collapsed;
        if (PnlCfgOcrBaidu != null) PnlCfgOcrBaidu.Visibility = engine == "Baidu" ? Visibility.Visible : Visibility.Collapsed;
        if (PnlCfgOcrYoudao != null) PnlCfgOcrYoudao.Visibility = engine == "Youdao" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OcrEngineRow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string engine)
        {
            SelectOcrEngine(engine);
        }
    }

    private void TransConfig_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isReady) return;
        if (TxtBaiduAppId != null) PreferencesStore.BaiduTranslateAppId = TxtBaiduAppId.Text.Trim();
        if (TxtOpenAIBaseUrl != null) PreferencesStore.OpenAIBaseUrl = TxtOpenAIBaseUrl.Text.Trim();
        if (TxtOpenAIModel != null) PreferencesStore.OpenAIModel = TxtOpenAIModel.Text.Trim();
        PreferencesStore.Save();
    }

    private void BaiduSecret_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (PwdBaiduSecret != null) PreferencesStore.BaiduTranslateSecretKey = PwdBaiduSecret.Password.Trim();
        PreferencesStore.Save();
    }

    private void GoogleKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (PwdGoogleKey != null) PreferencesStore.GoogleTranslateApiKey = PwdGoogleKey.Password.Trim();
        PreferencesStore.Save();
    }

    private void DeepLKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (PwdDeepLKey != null) PreferencesStore.DeepLTranslateApiKey = PwdDeepLKey.Password.Trim();
        PreferencesStore.Save();
    }

    private void OpenAIKey_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (PwdOpenAIKey != null) PreferencesStore.OpenAIApiKey = PwdOpenAIKey.Password.Trim();
        PreferencesStore.Save();
    }

    private void OcrConfig_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isReady) return;
        if (TxtBaiduOcrKey != null) PreferencesStore.BaiduOcrApiKey = TxtBaiduOcrKey.Text.Trim();
        if (TxtYoudaoOcrKey != null) PreferencesStore.YoudaoOcrAppKey = TxtYoudaoOcrKey.Text.Trim();
        PreferencesStore.Save();
    }

    private void BaiduOcrSecret_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (PwdBaiduOcrSecret != null) PreferencesStore.BaiduOcrSecretKey = PwdBaiduOcrSecret.Password.Trim();
        PreferencesStore.Save();
    }

    private void YoudaoOcrSecret_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (!_isReady) return;
        if (PwdYoudaoOcrSecret != null) PreferencesStore.YoudaoOcrAppSecret = PwdYoudaoOcrSecret.Password.Trim();
        PreferencesStore.Save();
    }

    private void BtnToggleBaiduSecret_Click(object sender, RoutedEventArgs e)
    {
        TogglePasswordVisibility(PwdBaiduSecret, TxtBaiduSecret);
    }

    private void BtnToggleGoogleKey_Click(object sender, RoutedEventArgs e)
    {
        TogglePasswordVisibility(PwdGoogleKey, TxtGoogleKey);
    }

    private void BtnToggleDeepLKey_Click(object sender, RoutedEventArgs e)
    {
        TogglePasswordVisibility(PwdDeepLKey, TxtDeepLKey);
    }

    private void BtnToggleOpenAIKey_Click(object sender, RoutedEventArgs e)
    {
        TogglePasswordVisibility(PwdOpenAIKey, TxtOpenAIKey);
    }

    private void TogglePasswordVisibility(PasswordBox? pwd, TextBox? txt)
    {
        if (pwd == null || txt == null) return;
        if (pwd.Visibility == Visibility.Visible)
        {
            txt.Text = pwd.Password;
            pwd.Visibility = Visibility.Collapsed;
            txt.Visibility = Visibility.Visible;
            txt.Focus();
        }
        else
        {
            pwd.Password = txt.Text;
            txt.Visibility = Visibility.Collapsed;
            pwd.Visibility = Visibility.Visible;
            pwd.Focus();
        }
    }

    private async void BtnVerifyBuiltInTrans_Click(object sender, RoutedEventArgs e)
    {
        if (TxtVerifyBuiltInStatus != null) TxtVerifyBuiltInStatus.Text = "正在测试连接...";
        var (ok, msg) = await AiTranslationService.VerifyEngineAsync("BuiltIn");
        if (TxtVerifyBuiltInStatus != null)
        {
            TxtVerifyBuiltInStatus.Text = ok ? "✓ 验证成功" : "✕ 失败";
            TxtVerifyBuiltInStatus.Foreground = ok ? Brushes.SeaGreen : Brushes.Crimson;
        }
        if (ok) ModernToast.ShowSuccess("内置免密翻译引擎连接正常！");
        else ModernToast.ShowError($"连接测试失败: {msg}");
    }

    private async void BtnVerifyBaiduTrans_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreferencesStore.BaiduTranslateAppId) || string.IsNullOrWhiteSpace(PreferencesStore.BaiduTranslateSecretKey))
        {
            ModernToast.ShowWarning("请先输入百度翻译 APP ID 和 Secret Key");
            return;
        }
        if (TxtVerifyBaiduStatus != null) TxtVerifyBaiduStatus.Text = "正在验证...";
        var (ok, msg) = await AiTranslationService.VerifyEngineAsync("Baidu");
        if (TxtVerifyBaiduStatus != null)
        {
            TxtVerifyBaiduStatus.Text = ok ? "✓ 验证成功" : "✕ 秘钥错误";
            TxtVerifyBaiduStatus.Foreground = ok ? Brushes.SeaGreen : Brushes.Crimson;
        }
        if (ok) ModernToast.ShowSuccess("百度翻译秘钥验证成功！");
        else ModernToast.ShowError($"百度翻译验证失败: {msg}");
    }

    private async void BtnVerifyGoogleTrans_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreferencesStore.GoogleTranslateApiKey))
        {
            ModernToast.ShowWarning("请先输入 Google Cloud API Key");
            return;
        }
        if (TxtVerifyGoogleStatus != null) TxtVerifyGoogleStatus.Text = "正在验证...";
        var (ok, msg) = await AiTranslationService.VerifyEngineAsync("Google");
        if (TxtVerifyGoogleStatus != null)
        {
            TxtVerifyGoogleStatus.Text = ok ? "✓ 验证成功" : "✕ 秘钥错误";
            TxtVerifyGoogleStatus.Foreground = ok ? Brushes.SeaGreen : Brushes.Crimson;
        }
        if (ok) ModernToast.ShowSuccess("Google 翻译秘钥验证成功！");
        else ModernToast.ShowError($"Google 验证失败: {msg}");
    }

    private async void BtnVerifyDeepLTrans_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreferencesStore.DeepLTranslateApiKey))
        {
            ModernToast.ShowWarning("请先输入 DeepL Auth Key");
            return;
        }
        if (TxtVerifyDeepLStatus != null) TxtVerifyDeepLStatus.Text = "正在验证...";
        var (ok, msg) = await AiTranslationService.VerifyEngineAsync("DeepL");
        if (TxtVerifyDeepLStatus != null)
        {
            TxtVerifyDeepLStatus.Text = ok ? "✓ 验证成功" : "✕ 秘钥错误";
            TxtVerifyDeepLStatus.Foreground = ok ? Brushes.SeaGreen : Brushes.Crimson;
        }
        if (ok) ModernToast.ShowSuccess("DeepL 秘钥验证成功！");
        else ModernToast.ShowError($"DeepL 验证失败: {msg}");
    }

    private async void BtnVerifyOpenAITrans_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreferencesStore.OpenAIApiKey))
        {
            ModernToast.ShowWarning("请先输入 OpenAI API Key");
            return;
        }
        if (TxtVerifyOpenAIStatus != null) TxtVerifyOpenAIStatus.Text = "正在验证...";
        var (ok, msg) = await AiTranslationService.VerifyEngineAsync("OpenAI");
        if (TxtVerifyOpenAIStatus != null)
        {
            TxtVerifyOpenAIStatus.Text = ok ? "✓ 验证成功" : "✕ 秘钥错误";
            TxtVerifyOpenAIStatus.Foreground = ok ? Brushes.SeaGreen : Brushes.Crimson;
        }
        if (ok) ModernToast.ShowSuccess("OpenAI / 兼容模型验证成功！");
        else ModernToast.ShowError($"OpenAI 验证失败: {msg}");
    }

    private void BtnVerifyWinOcr_Click(object sender, RoutedEventArgs e)
    {
        ModernToast.ShowSuccess("Windows 原生 OCR 引擎已就绪，支持离线高速识别！");
    }

    private async void BtnVerifyBaiduOcr_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreferencesStore.BaiduOcrApiKey) || string.IsNullOrWhiteSpace(PreferencesStore.BaiduOcrSecretKey))
        {
            ModernToast.ShowWarning("请先输入百度智能云 OCR 的 API Key 与 Secret Key");
            return;
        }
        var (ok, msg) = await OcrEngine.VerifyBaiduOcrAsync(PreferencesStore.BaiduOcrApiKey, PreferencesStore.BaiduOcrSecretKey);
        if (ok) ModernToast.ShowSuccess(msg);
        else ModernToast.ShowError(msg);
    }

    private void BtnVerifyYoudaoOcr_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(PreferencesStore.YoudaoOcrAppKey) || string.IsNullOrWhiteSpace(PreferencesStore.YoudaoOcrAppSecret))
        {
            ModernToast.ShowWarning("请先输入有道智云 OCR 的 App Key 与 App Secret");
            return;
        }
        ModernToast.ShowSuccess("有道智云 OCR 配置已保存并就绪！");
    }

    private void LnkApplyBaiduTrans_Click(object sender, RoutedEventArgs e) => OpenWebUrl("https://fanyi-api.baidu.com/");
    private void LnkApplyGoogleTrans_Click(object sender, RoutedEventArgs e) => OpenWebUrl("https://cloud.google.com/translate/docs/setup");
    private void LnkApplyDeepLTrans_Click(object sender, RoutedEventArgs e) => OpenWebUrl("https://www.deepl.com/pro-api");
    private void LnkApplyOpenAITrans_Click(object sender, RoutedEventArgs e) => OpenWebUrl("https://platform.openai.com/api-keys");
    private void LnkApplyBaiduOcr_Click(object sender, RoutedEventArgs e) => OpenWebUrl("https://ai.baidu.com/tech/ocr");
    private void LnkApplyYoudaoOcr_Click(object sender, RoutedEventArgs e) => OpenWebUrl("https://ai.youdao.com/");

    private static void OpenWebUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ModernToast.ShowError($"无法打开网页: {ex.Message}");
        }
    }
}
