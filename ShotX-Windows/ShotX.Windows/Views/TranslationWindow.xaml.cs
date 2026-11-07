using System;
using System.Net.Http;
using System.Speech.Synthesis;
using System.Text.Json;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using ShotX.Windows.Core;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public partial class TranslationWindow : Window
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    private readonly DispatcherTimer _debounceTimer;
    private CancellationTokenSource? _cts;
    private SpeechSynthesizer? _synth;
    private bool _isReady = false;

    public TranslationWindow()
    {
        _debounceTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(380)
        };
        _debounceTimer.Tick += DebounceTimer_Tick;

        InitializeComponent();

        _isReady = true;
        Loaded += (_, _) =>
        {
            UpdateActiveEngineBadge();
            TxtSource?.Focus();
            if (!string.IsNullOrWhiteSpace(TxtSource?.Text))
            {
                TriggerTranslation();
            }
        };
    }

    private void UpdateActiveEngineBadge()
    {
        string engine = PreferencesStore.ActiveTranslationEngine ?? "BuiltIn";
        string displayName = engine switch
        {
            "Baidu" => "百度翻译",
            "BuiltIn" => "内置免密",
            "Google" => "谷歌翻译",
            "DeepL" => "DeepL",
            "OpenAI" => "OpenAI",
            _ => engine
        };
        if (TxtActiveEngineBadge != null)
        {
            TxtActiveEngineBadge.Text = displayName;
        }
    }

    public TranslationWindow(string sourceText) : this()
    {
        SetSourceText(sourceText);
    }

    public void SetSourceText(string text)
    {
        if (TxtSource == null) return;
        TxtSource.Text = text ?? string.Empty;
        TxtSource.CaretIndex = TxtSource.Text.Length;
        if (TxtSourcePlaceholder != null)
        {
            TxtSourcePlaceholder.Visibility = string.IsNullOrEmpty(TxtSource.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TxtCharCount != null)
        {
            TxtCharCount.Text = $"{TxtSource.Text.Length} 字";
        }
        if (IsLoaded && !string.IsNullOrWhiteSpace(TxtSource.Text))
        {
            TriggerTranslation();
        }
    }

    public void SetSourceAndResult(string source, string? result)
    {
        if (TxtSource == null) return;
        TxtSource.Text = source ?? string.Empty;
        TxtSource.CaretIndex = TxtSource.Text.Length;
        if (TxtSourcePlaceholder != null)
        {
            TxtSourcePlaceholder.Visibility = string.IsNullOrEmpty(TxtSource.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TxtCharCount != null)
        {
            TxtCharCount.Text = $"{TxtSource.Text.Length} 字";
        }
        if (!string.IsNullOrWhiteSpace(result))
        {
            TxtResult.Text = result;
        }
        else if (IsLoaded && !string.IsNullOrWhiteSpace(TxtSource.Text))
        {
            TriggerTranslation();
        }
    }

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnPin_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        if (PinPath != null)
        {
            PinPath.Fill = Topmost
                ? new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 122, 255))
                : new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(150, 150, 150));
        }
    }

    private void BtnHistory_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            // 点击翻译窗口右上角历史记录：直接跳转到设置中心的历史记录->翻译分类，且复用已有设置窗口不重复打开 (解决需求 6)
            app.ShowPreferences(initialTabIsHistory: true, filterType: HistoryType.Translation);
        }
    }

    private void BtnSettings_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app)
        {
            app.ShowPreferences(initialTabIsHistory: false);
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
        else if (e.Key == Key.Enter && (Keyboard.Modifiers == ModifierKeys.Control || Keyboard.Modifiers == ModifierKeys.None))
        {
            TriggerTranslation();
        }
    }

    private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isReady || TxtSource == null || _debounceTimer == null) return;

        string text = TxtSource.Text;
        if (TxtSourcePlaceholder != null)
        {
            TxtSourcePlaceholder.Visibility = string.IsNullOrEmpty(text) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (TxtCharCount != null)
        {
            TxtCharCount.Text = $"{text.Length} 字";
        }

        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private void DebounceTimer_Tick(object? sender, EventArgs e)
    {
        _debounceTimer.Stop();
        TriggerTranslation();
    }

    private void Lang_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_isReady || !IsLoaded) return;
        TriggerTranslation();
    }

    private void BtnSwap_Click(object sender, RoutedEventArgs e)
    {
        if (CmbSourceLang == null || CmbTargetLang == null) return;

        // 1. 将译文框内容替换到输入框后并翻译 (解决需求 3)
        string currentResult = TxtResult.Text.Trim();
        bool hasValidResult = !string.IsNullOrEmpty(currentResult) &&
                              currentResult != "正在翻译中..." &&
                              !currentResult.StartsWith("（网络翻译接口繁忙");

        if (hasValidResult)
        {
            TxtSource.Text = currentResult;
            TxtSource.CaretIndex = TxtSource.Text.Length;
        }

        // 2. 互换语言选项
        string? srcName = (CmbSourceLang.SelectedItem as ComboBoxItem)?.Content?.ToString();
        string? tgtName = (CmbTargetLang.SelectedItem as ComboBoxItem)?.Content?.ToString();

        SelectLanguageInCombo(CmbSourceLang, tgtName, currentResult, true);
        SelectLanguageInCombo(CmbTargetLang, srcName, currentResult, false);

        // 3. 立即翻译
        TriggerTranslation();
    }

    private void SelectLanguageInCombo(ComboBox combo, string? targetLangName, string sampleText, bool isSource)
    {
        if (string.IsNullOrWhiteSpace(targetLangName) || targetLangName.Contains("自动"))
        {
            bool hasChinese = sampleText.Any(c => (c >= 0x4e00 && c <= 0x9fff) || (c >= 0x3400 && c <= 0x4dbf));
            string preferred = isSource ? (hasChinese ? "中文" : "英语") : (hasChinese ? "英语" : "中文");
            MatchComboItem(combo, preferred);
            return;
        }

        MatchComboItem(combo, targetLangName);
    }

    private static void MatchComboItem(ComboBox combo, string langKeyword)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item && item.Content != null)
            {
                string text = item.Content.ToString() ?? "";
                if (text.Contains(langKeyword.Replace(" (简体)", "").Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    combo.SelectedIndex = i;
                    return;
                }
            }
        }
        if (combo.Items.Count > 0) combo.SelectedIndex = 0;
    }

    private string GetLangCode(string? langName, bool isSource, string text = "")
    {
        if (string.IsNullOrWhiteSpace(langName) || langName.Contains("自动"))
        {
            if (isSource) return "auto";

            // 检测到中文自动翻译为英文，检测到英文自动翻译为中文 (解决需求 3)
            bool hasChinese = !string.IsNullOrEmpty(text) && text.Any(c => (c >= 0x4e00 && c <= 0x9fff) || (c >= 0x3400 && c <= 0x4dbf));
            return hasChinese ? "en" : "zh-CN";
        }

        if (langName.Contains("中文")) return "zh-CN";
        if (langName.Contains("英语")) return "en";
        if (langName.Contains("日语")) return "ja";
        if (langName.Contains("韩语")) return "ko";
        if (langName.Contains("法语")) return "fr";
        if (langName.Contains("德语")) return "de";
        if (langName.Contains("俄语")) return "ru";
        if (langName.Contains("西班牙语")) return "es";
        return isSource ? "auto" : "zh-CN";
    }

    private async void TriggerTranslation()
    {
        if (!_isReady || TxtSource == null || TxtResult == null || CmbSourceLang == null || CmbTargetLang == null)
        {
            return;
        }

        string text = TxtSource.Text.Trim();
        if (string.IsNullOrEmpty(text))
        {
            TxtResult.Text = string.Empty;
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        string sl = GetLangCode((CmbSourceLang.SelectedItem as ComboBoxItem)?.Content?.ToString(), true, text);
        string tl = GetLangCode((CmbTargetLang.SelectedItem as ComboBoxItem)?.Content?.ToString(), false, text);

        if (sl == tl && sl != "auto")
        {
            tl = (sl == "zh-CN") ? "en" : "zh-CN";
        }

        TxtResult.Text = "正在翻译中...";

        try
        {
            string result = await AiTranslationService.TranslateAsync(text, sl, tl, ct);
            if (!string.IsNullOrWhiteSpace(result))
            {
                TxtResult.Text = result;
                HistoryStore.Add(HistoryType.Translation, text, result);
                return;
            }
            TxtResult.Text = "（翻译接口繁忙或未配置有效密钥，请重试）";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            if (TxtResult != null)
            {
                TxtResult.Text = "（翻译遇到问题，请检查网络）";
            }
        }
    }

    private void BtnSpeakSource_Click(object sender, RoutedEventArgs e)
    {
        if (TxtSource != null) Speak(TxtSource.Text);
    }

    private void BtnSpeakResult_Click(object sender, RoutedEventArgs e)
    {
        if (TxtResult != null) Speak(TxtResult.Text);
    }

    private void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        try
        {
            _synth ??= new SpeechSynthesizer();
            _synth.SpeakAsyncCancelAll();
            _synth.SpeakAsync(text);
        }
        catch { }
    }

    private void BtnCopySource_Click(object sender, RoutedEventArgs e)
    {
        if (TxtSource != null && !string.IsNullOrEmpty(TxtSource.Text))
        {
            try { Clipboard.SetText(TxtSource.Text); } catch { }
        }
    }

    private void BtnCopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (TxtResult != null && !string.IsNullOrEmpty(TxtResult.Text))
        {
            try { Clipboard.SetText(TxtResult.Text); } catch { }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        try
        {
            _debounceTimer?.Stop();
            _cts?.Cancel();
            _synth?.Dispose();
        }
        catch { }
    }
}
