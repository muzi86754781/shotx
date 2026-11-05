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
            TxtSource?.Focus();
            if (!string.IsNullOrWhiteSpace(TxtSource?.Text))
            {
                TriggerTranslation();
            }
        };
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
        if (TxtCharCount != null)
        {
            TxtCharCount.Text = $"{TxtSource.Text.Length} 字";
        }
        if (IsLoaded)
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
            app.ShowPreferences(initialTabIsHistory: true);
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
        int src = CmbSourceLang.SelectedIndex;
        int tgt = CmbTargetLang.SelectedIndex;

        if (src > 0 && tgt > 0)
        {
            CmbSourceLang.SelectedIndex = tgt;
            CmbTargetLang.SelectedIndex = src;
        }
        else if (src == 0 && tgt > 0)
        {
            CmbSourceLang.SelectedIndex = tgt;
            CmbTargetLang.SelectedIndex = 1;
        }
        else
        {
            CmbSourceLang.SelectedIndex = 2;
            CmbTargetLang.SelectedIndex = 2;
        }
    }

    private string GetLangCode(string? langName, bool isSource)
    {
        if (string.IsNullOrWhiteSpace(langName)) return isSource ? "auto" : "zh-CN";
        if (langName.Contains("自动")) return isSource ? "auto" : "zh-CN";
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

        string sl = GetLangCode((CmbSourceLang.SelectedItem as ComboBoxItem)?.Content?.ToString(), true);
        string tl = GetLangCode((CmbTargetLang.SelectedItem as ComboBoxItem)?.Content?.ToString(), false);

        TxtResult.Text = "正在翻译中...";

        try
        {
            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";
            var resp = await _httpClient.GetAsync(url, ct);
            if (resp.IsSuccessStatusCode)
            {
                string json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                {
                    var segs = root[0];
                    var sb = new System.Text.StringBuilder();
                    foreach (var seg in segs.EnumerateArray())
                    {
                        if (seg.ValueKind == JsonValueKind.Array && seg.GetArrayLength() > 0)
                        {
                            sb.Append(seg[0].GetString());
                        }
                    }
                    string result = sb.ToString();
                    TxtResult.Text = result;

                    HistoryStore.Add(HistoryType.Translation, text, result);
                    return;
                }
            }
            TxtResult.Text = "（网络翻译接口繁忙，请检查网络连接）";
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
