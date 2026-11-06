using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using BitmapDecoder = Windows.Graphics.Imaging.BitmapDecoder;
using WinRtOcrEngine = Windows.Media.Ocr.OcrEngine;

namespace ShotX.Windows.Core;

public static class OcrEngine
{
    private static readonly HttpClient _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

    public static async Task<string> RecognizeAsync(Bitmap croppedBitmap)
    {
        if (croppedBitmap == null || croppedBitmap.Width <= 2 || croppedBitmap.Height <= 2)
        {
            return string.Empty;
        }

        // 1. 优先使用增强预处理后的 Windows 原生离线 OCR (支持 2x~3x 放大与留白边，大幅提升小字号与中英文混合识别率)
        try
        {
            using var enhanced = CreatePreprocessedBitmap(croppedBitmap);
            string localResult = await RecognizeWithWinRtAsync(enhanced);
            if (!string.IsNullOrWhiteSpace(localResult))
            {
                return CleanOcrText(localResult);
            }

            // 如果增强图未识别出结果，尝试原图再跑一次
            string rawResult = await RecognizeWithWinRtAsync(croppedBitmap);
            if (!string.IsNullOrWhiteSpace(rawResult))
            {
                return CleanOcrText(rawResult);
            }
        }
        catch
        {
        }

        // 2. 自动云端高精度 OCR 兜底 (解决部分精简版 Windows 未安装中文 OCR 语言包导致识别为空的问题)
        try
        {
            string cloudResult = await RecognizeWithCloudFallbackAsync(croppedBitmap);
            if (!string.IsNullOrWhiteSpace(cloudResult))
            {
                return cloudResult.Trim();
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    private static Bitmap CreatePreprocessedBitmap(Bitmap src)
    {
        // 对小尺寸或常规字体截图进行智能放大 + 白边填充，解决 WinRT OCR 对小字或贴边文字漏识别的问题
        double scale = 1.0;
        if (src.Height < 80 || src.Width < 160)
        {
            scale = 2.5;
        }
        else if (src.Height < 260)
        {
            scale = 2.0;
        }
        else if (src.Height < 500)
        {
            scale = 1.5;
        }

        int pad = 16;
        int targetW = Math.Max(32, (int)Math.Round(src.Width * scale)) + pad * 2;
        int targetH = Math.Max(32, (int)Math.Round(src.Height * scale)) + pad * 2;

        // 限制最大边长不超过 WinRT OCR 硬件上限 (4096)
        if (targetW > 3800 || targetH > 3800)
        {
            return new Bitmap(src);
        }

        var dst = new Bitmap(targetW, targetH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dst);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.SmoothingMode = SmoothingMode.HighQuality;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(src, new Rectangle(pad, pad, targetW - pad * 2, targetH - pad * 2));
        return dst;
    }

    private static async Task<string> RecognizeWithWinRtAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        ms.Position = 0;

        using var ras = ms.AsRandomAccessStream();
        var decoder = await BitmapDecoder.CreateAsync(ras);
        using var rawSoftBmp = await decoder.GetSoftwareBitmapAsync();
        using var softwareBitmap = SoftwareBitmap.Convert(rawSoftBmp, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

        // 优先尝试简体中文/繁体中文引擎（中文引擎同时兼容识别英文与数字），再回退到系统语言及所有已安装语言包
        WinRtOcrEngine? engine = TryGetPreferredOcrEngine();
        if (engine != null)
        {
            var res = await engine.RecognizeAsync(softwareBitmap);
            if (res != null && res.Lines != null && res.Lines.Count > 0)
            {
                var sb = new StringBuilder();
                foreach (var line in res.Lines)
                {
                    if (!string.IsNullOrWhiteSpace(line.Text))
                    {
                        sb.AppendLine(line.Text.Trim());
                    }
                }
                string text = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return text;
                }
            }
        }

        // 遍历所有可用语言包尝试识别
        foreach (var lang in WinRtOcrEngine.AvailableRecognizerLanguages)
        {
            try
            {
                var altEngine = WinRtOcrEngine.TryCreateFromLanguage(lang);
                if (altEngine == null) continue;
                var res = await altEngine.RecognizeAsync(softwareBitmap);
                if (res != null && res.Lines != null && res.Lines.Count > 0)
                {
                    var sb = new StringBuilder();
                    foreach (var line in res.Lines)
                    {
                        if (!string.IsNullOrWhiteSpace(line.Text))
                        {
                            sb.AppendLine(line.Text.Trim());
                        }
                    }
                    string text = sb.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        return text;
                    }
                }
            }
            catch
            {
            }
        }

        return string.Empty;
    }

    private static WinRtOcrEngine? TryGetPreferredOcrEngine()
    {
        try
        {
            var available = WinRtOcrEngine.AvailableRecognizerLanguages;
            if (available != null && available.Count > 0)
            {
                // 优先选择 zh-Hans / zh-CN (可同时识别中英文)
                var zhHans = available.FirstOrDefault(l =>
                    l.LanguageTag.StartsWith("zh-Hans", StringComparison.OrdinalIgnoreCase) ||
                    l.LanguageTag.Equals("zh-CN", StringComparison.OrdinalIgnoreCase) ||
                    l.LanguageTag.StartsWith("zh", StringComparison.OrdinalIgnoreCase));
                if (zhHans != null)
                {
                    var eng = WinRtOcrEngine.TryCreateFromLanguage(zhHans);
                    if (eng != null) return eng;
                }
            }

            var profileEngine = WinRtOcrEngine.TryCreateFromUserProfileLanguages();
            if (profileEngine != null) return profileEngine;

            string[] tags = { "zh-Hans-CN", "zh-CN", "zh-Hans", "en-US", "en-GB", "ja-JP" };
            foreach (var tag in tags)
            {
                try
                {
                    var lang = new Language(tag);
                    if (WinRtOcrEngine.IsLanguageSupported(lang))
                    {
                        var eng = WinRtOcrEngine.TryCreateFromLanguage(lang);
                        if (eng != null) return eng;
                    }
                }
                catch { }
            }
        }
        catch
        {
        }
        return null;
    }

    private static string CleanOcrText(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        // 移除 WinRT OCR 在中文字符之间自动插入的多余空格，保留英文单词之间的空格
        return Regex.Replace(raw, @"(?<=[\u4e00-\u9fa5])\s+(?=[\u4e00-\u9fa5])", "").Trim();
    }

    private static async Task<string> RecognizeWithCloudFallbackAsync(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        string base64Image = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());

        using var form = new MultipartFormDataContent
        {
            { new StringContent("helloworld"), "apikey" },
            { new StringContent("chs"), "language" },
            { new StringContent("true"), "isOverlayRequired" },
            { new StringContent("true"), "scale" },
            { new StringContent("2"), "OCREngine" },
            { new StringContent(base64Image), "base64Image" }
        };

        using var resp = await _httpClient.PostAsync("https://api.ocr.space/parse/image", form);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("ParsedResults", out var results) &&
            results.ValueKind == JsonValueKind.Array &&
            results.GetArrayLength() > 0)
        {
            var first = results[0];
            if (first.TryGetProperty("ParsedText", out var parsedText))
            {
                return parsedText.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}

public static class OcrHelper
{
    public static Task<string> RecognizeTextAsync(Bitmap bitmap) => OcrEngine.RecognizeAsync(bitmap);
}
