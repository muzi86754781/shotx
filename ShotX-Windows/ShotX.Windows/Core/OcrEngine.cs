using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
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

        string engine = PreferencesStore.ActiveOcrEngine ?? "System";

        // 1. 若选择并配置了【百度智能云 OCR】
        if (engine.Equals("Baidu", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(PreferencesStore.BaiduOcrApiKey) &&
            !string.IsNullOrWhiteSpace(PreferencesStore.BaiduOcrSecretKey))
        {
            try
            {
                string baiduResult = await RecognizeWithBaiduAsync(croppedBitmap, PreferencesStore.BaiduOcrApiKey, PreferencesStore.BaiduOcrSecretKey);
                if (!string.IsNullOrWhiteSpace(baiduResult))
                {
                    return baiduResult.Trim();
                }
            }
            catch { }
        }
        // 2. 若选择并配置了【有道智云 OCR】
        else if (engine.Equals("Youdao", StringComparison.OrdinalIgnoreCase) &&
                 !string.IsNullOrWhiteSpace(PreferencesStore.YoudaoOcrAppKey) &&
                 !string.IsNullOrWhiteSpace(PreferencesStore.YoudaoOcrAppSecret))
        {
            try
            {
                string youdaoResult = await RecognizeWithYoudaoAsync(croppedBitmap, PreferencesStore.YoudaoOcrAppKey, PreferencesStore.YoudaoOcrAppSecret);
                if (!string.IsNullOrWhiteSpace(youdaoResult))
                {
                    return youdaoResult.Trim();
                }
            }
            catch { }
        }

        // 3. 增强预处理后的 Windows 原生离线 OCR (系统离线模式或云端异常后的离线兜底)
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

        // 4. 自动云端高精度 OCR 兜底 (解决部分精简版 Windows 未安装中文 OCR 语言包导致识别为空的问题)
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

    public static async Task<string> RecognizeWithBaiduAsync(Bitmap bmp, string apiKey, string secretKey)
    {
        string token = await GetBaiduOcrTokenAsync(apiKey, secretKey);
        if (string.IsNullOrWhiteSpace(token)) return string.Empty;

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        string base64 = Convert.ToBase64String(ms.ToArray());

        string url = $"https://aip.baidubce.com/rest/2.0/ocr/v1/general_basic?access_token={token}";
        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("image", base64)
        });

        var resp = await _httpClient.PostAsync(url, content);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("words_result", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var item in results.EnumerateArray())
            {
                if (item.TryGetProperty("words", out var w))
                {
                    sb.AppendLine(w.GetString());
                }
            }
            return sb.ToString().TrimEnd();
        }
        return string.Empty;
    }

    public static async Task<string> GetBaiduOcrTokenAsync(string apiKey, string secretKey)
    {
        string tokenUrl = $"https://aip.baidubce.com/oauth/2.0/token?grant_type=client_credentials&client_id={apiKey.Trim()}&client_secret={secretKey.Trim()}";
        var resp = await _httpClient.PostAsync(tokenUrl, null);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("access_token", out var tok))
        {
            return tok.GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    public static async Task<(bool ok, string msg)> VerifyBaiduOcrAsync(string apiKey, string secretKey)
    {
        try
        {
            string token = await GetBaiduOcrTokenAsync(apiKey, secretKey);
            if (!string.IsNullOrWhiteSpace(token))
            {
                return (true, "验证成功！百度智能云 OCR 通信正常");
            }
            return (false, "验证失败：API Key 或 Secret Key 无效，请核对。");
        }
        catch (Exception ex)
        {
            return (false, $"连接失败: {ex.Message}");
        }
    }

    public static async Task<string> RecognizeWithYoudaoAsync(Bitmap bmp, string appKey, string appSecret)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        string base64 = Convert.ToBase64String(ms.ToArray());
        string salt = Guid.NewGuid().ToString("N");
        string curtime = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        string signStr = appKey.Trim() + (base64.Length <= 20 ? base64 : (base64.Substring(0, 10) + base64.Length + base64.Substring(base64.Length - 10))) + salt + curtime + appSecret.Trim();
        using var sha256 = SHA256.Create();
        byte[] hash = sha256.ComputeHash(Encoding.UTF8.GetBytes(signStr));
        var sbSign = new StringBuilder();
        foreach (byte b in hash) sbSign.Append(b.ToString("x2"));
        string sign = sbSign.ToString();

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("img", base64),
            new KeyValuePair<string, string>("langType", "auto"),
            new KeyValuePair<string, string>("detectType", "10012"),
            new KeyValuePair<string, string>("imageType", "1"),
            new KeyValuePair<string, string>("appKey", appKey.Trim()),
            new KeyValuePair<string, string>("salt", salt),
            new KeyValuePair<string, string>("sign", sign),
            new KeyValuePair<string, string>("signType", "v3"),
            new KeyValuePair<string, string>("curtime", curtime)
        });

        var resp = await _httpClient.PostAsync("https://openapi.youdao.com/ocrapi", form);
        if (!resp.IsSuccessStatusCode) return string.Empty;
        string json = await resp.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("Result", out var resObj) && resObj.TryGetProperty("regions", out var regions))
        {
            var sb = new StringBuilder();
            foreach (var reg in regions.EnumerateArray())
            {
                if (reg.TryGetProperty("lines", out var lines))
                {
                    foreach (var line in lines.EnumerateArray())
                    {
                        if (line.TryGetProperty("text", out var t))
                        {
                            sb.AppendLine(t.GetString());
                        }
                    }
                }
            }
            return sb.ToString().TrimEnd();
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
