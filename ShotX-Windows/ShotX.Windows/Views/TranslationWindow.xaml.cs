using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public partial class TranslationWindow : Window
{
    private static readonly HttpClient HttpClient = new();
    private CancellationTokenSource? _cts;

    public TranslationWindow(string initialText = "")
    {
        InitializeComponent();
        if (!string.IsNullOrEmpty(initialText))
        {
            TxtSource.Text = initialText;
            DoTranslate(initialText);
        }
        else
        {
            // If clipboard has text, auto fill
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string clip = System.Windows.Clipboard.GetText().Trim();
                    if (!string.IsNullOrEmpty(clip) && clip.Length < 1000)
                    {
                        TxtSource.Text = clip;
                        DoTranslate(clip);
                    }
                }
            }
            catch
            {
            }
        }
        TxtSource.Focus();
    }

    private void TxtSource_TextChanged(object sender, TextChangedEventArgs e)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        Task.Delay(400, token).ContinueWith(t =>
        {
            if (!t.IsCanceled)
            {
                Dispatcher.Invoke(() => DoTranslate(TxtSource.Text));
            }
        }, token);
    }

    private async void DoTranslate(string query)
    {
        query = query.Trim();
        if (string.IsNullOrEmpty(query))
        {
            TxtResult.Text = "";
            return;
        }

        try
        {
            // Free multi-language translation API endpoint
            string targetLang = (CmbTargetLang.SelectedIndex == 2) ? "en" : (CmbTargetLang.SelectedIndex == 3 ? "ja" : (CmbTargetLang.SelectedIndex == 4 ? "ko" : "zh"));
            // Auto swap: if Chinese, target English; if English, target Chinese
            bool isChinese = query.Any(c => c >= 0x4E00 && c <= 0x9FA5);
            if (CmbTargetLang.SelectedIndex == 0)
            {
                targetLang = isChinese ? "en" : "zh";
            }

            string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl=auto&tl={targetLang}&dt=t&q={Uri.EscapeDataString(query)}";
            string json = await HttpClient.GetStringAsync(url);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
            {
                var sentences = root[0];
                var sb = new System.Text.StringBuilder();
                foreach (var s in sentences.EnumerateArray())
                {
                    if (s.GetArrayLength() > 0)
                    {
                        sb.Append(s[0].GetString());
                    }
                }
                TxtResult.Text = sb.ToString();
                return;
            }

            TxtResult.Text = query;
        }
        catch
        {
            TxtResult.Text = "（网络连接错误，无法完成翻译）";
        }
    }

    private void BtnTranslate_Click(object sender, RoutedEventArgs e)
    {
        DoTranslate(TxtSource.Text);
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(TxtResult.Text))
        {
            System.Windows.Clipboard.SetText(TxtResult.Text);
        }
        Close();
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

    private void Window_Deactivated(object sender, EventArgs e)
    {
        // 失焦第 1 次点击直接响应关闭 (与 macOS 版一模一样)
        Close();
    }
}

