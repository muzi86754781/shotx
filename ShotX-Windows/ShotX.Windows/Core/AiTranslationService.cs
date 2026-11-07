using System;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ShotX.Windows.Core;

/// <summary>
/// 负责 ShotX 多平台机器翻译调度与 API 接入（内置免密钥、百度、谷歌、有道、DeepL、OpenAI）。
/// 严格遵循多引擎可配置与信息保留机制（解决需求 3）。
/// </summary>
public static class AiTranslationService
{
    private static readonly HttpClient _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

    public static async Task<string> TranslateAsync(string text, string sl, string tl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var prefs = PreferencesStore.Current;
        string engine = prefs.ActiveTranslationEngine ?? "BuiltIn";

        try
        {
            switch (engine.ToLowerInvariant())
            {
                case "baidu":
                    if (!string.IsNullOrWhiteSpace(prefs.BaiduTranslateAppId) && !string.IsNullOrWhiteSpace(prefs.BaiduTranslateSecretKey))
                    {
                        var res = await TranslateBaiduAsync(text, sl, tl, prefs.BaiduTranslateAppId, prefs.BaiduTranslateSecretKey, ct);
                        if (!string.IsNullOrWhiteSpace(res)) return res;
                    }
                    break;

                case "google":
                    if (!string.IsNullOrWhiteSpace(prefs.GoogleTranslateApiKey))
                    {
                        var res = await TranslateGoogleApiAsync(text, sl, tl, prefs.GoogleTranslateApiKey, ct);
                        if (!string.IsNullOrWhiteSpace(res)) return res;
                    }
                    break;

                case "deepl":
                    if (!string.IsNullOrWhiteSpace(prefs.DeepLTranslateApiKey))
                    {
                        var res = await TranslateDeepLAsync(text, sl, tl, prefs.DeepLTranslateApiKey, ct);
                        if (!string.IsNullOrWhiteSpace(res)) return res;
                    }
                    break;

                case "openai":
                    if (!string.IsNullOrWhiteSpace(prefs.OpenAIApiKey))
                    {
                        var res = await TranslateOpenAIAsync(text, sl, tl, prefs.OpenAIApiKey, prefs.OpenAIBaseUrl, prefs.OpenAIModel, ct);
                        if (!string.IsNullOrWhiteSpace(res)) return res;
                    }
                    break;
            }
        }
        catch { }

        // 默认内置免密钥通道兜底
        return await TranslateBuiltInAsync(text, sl, tl, ct);
    }

    public static async Task<string> TranslateBuiltInAsync(string text, string sl, string tl, CancellationToken ct = default)
    {
        string url = $"https://translate.googleapis.com/translate_a/single?client=gtx&sl={sl}&tl={tl}&dt=t&q={Uri.EscapeDataString(text)}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
        {
            var sentences = root[0];
            var sb = new StringBuilder();
            foreach (var s in sentences.EnumerateArray())
            {
                if (s.ValueKind == JsonValueKind.Array && s.GetArrayLength() > 0)
                {
                    sb.Append(s[0].GetString());
                }
            }
            return sb.ToString();
        }
        return string.Empty;
    }

    public static async Task<string> TranslateBaiduAsync(string text, string from, string to, string appId, string secretKey, CancellationToken ct = default)
    {
        string bFrom = from == "auto" ? "auto" : (from.StartsWith("zh") ? "zh" : from);
        string bTo = to.StartsWith("zh") ? "zh" : (to == "auto" ? "en" : to);
        string salt = DateTime.UtcNow.Ticks.ToString();
        string sign = Md5Hex($"{appId}{text}{salt}{secretKey}");

        string url = $"https://fanyi-api.baidu.com/api/trans/vip/translate?q={Uri.EscapeDataString(text)}&from={bFrom}&to={bTo}&appid={appId}&salt={salt}&sign={sign}";
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("trans_result", out var results) && results.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var it in results.EnumerateArray())
            {
                if (it.TryGetProperty("dst", out var dst))
                {
                    sb.AppendLine(dst.GetString());
                }
            }
            return sb.ToString().TrimEnd();
        }
        return string.Empty;
    }

    public static async Task<string> TranslateGoogleApiAsync(string text, string sl, string tl, string apiKey, CancellationToken ct = default)
    {
        string url = $"https://translation.googleapis.com/language/translate/v2?key={apiKey}";
        var payload = new
        {
            q = text,
            target = tl.StartsWith("zh") ? "zh-CN" : tl,
            format = "text"
        };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
        var resp = await _http.PostAsync(url, content, ct);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("translations", out var trans) &&
            trans.GetArrayLength() > 0)
        {
            return trans[0].GetProperty("translatedText").GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    public static async Task<string> TranslateDeepLAsync(string text, string sl, string tl, string authKey, CancellationToken ct = default)
    {
        string host = authKey.EndsWith(":fx") ? "https://api-free.deepl.com" : "https://api.deepl.com";
        string url = $"{host}/v2/translate";

        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Authorization", $"DeepL-Auth-Key {authKey.Trim()}");

        var targetLang = tl.ToUpperInvariant().StartsWith("ZH") ? "ZH" : "EN";
        var form = new MultipartFormDataContent
        {
            { new StringContent(text), "text" },
            { new StringContent(targetLang), "target_lang" }
        };
        req.Content = form;

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("translations", out var trans) && trans.GetArrayLength() > 0)
        {
            return trans[0].GetProperty("text").GetString() ?? string.Empty;
        }
        return string.Empty;
    }

    public static async Task<string> TranslateOpenAIAsync(string text, string sl, string tl, string apiKey, string? baseUrl, string? model, CancellationToken ct = default)
    {
        string url = (baseUrl?.TrimEnd('/') ?? "https://api.openai.com/v1") + "/chat/completions";
        using var req = new HttpRequestMessage(HttpMethod.Post, url);
        req.Headers.Add("Authorization", $"Bearer {apiKey.Trim()}");

        var payload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? "gpt-4o-mini" : model.Trim(),
            messages = new[]
            {
                new { role = "system", content = $"You are a professional translator. Translate the text to target language ({tl}). Reply with the translation only." },
                new { role = "user", content = text }
            },
            temperature = 0.2
        };
        req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return string.Empty;

        string json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            return choices[0].GetProperty("message").GetProperty("content").GetString()?.Trim() ?? string.Empty;
        }
        return string.Empty;
    }

    public static async Task<(bool success, string message)> VerifyEngineAsync(string engine)
    {
        var prefs = PreferencesStore.Current;
        try
        {
            string testWord = "Hello";
            string result = "";
            switch (engine.ToLowerInvariant())
            {
                case "builtin":
                    result = await TranslateBuiltInAsync(testWord, "en", "zh-CN");
                    break;
                case "baidu":
                    result = await TranslateBaiduAsync(testWord, "en", "zh", prefs.BaiduTranslateAppId, prefs.BaiduTranslateSecretKey);
                    break;
                case "google":
                    result = await TranslateGoogleApiAsync(testWord, "en", "zh-CN", prefs.GoogleTranslateApiKey);
                    break;
                case "deepl":
                    result = await TranslateDeepLAsync(testWord, "en", "zh", prefs.DeepLTranslateApiKey);
                    break;
                case "openai":
                    result = await TranslateOpenAIAsync(testWord, "en", "zh", prefs.OpenAIApiKey, prefs.OpenAIBaseUrl, prefs.OpenAIModel);
                    break;
            }

            if (!string.IsNullOrWhiteSpace(result))
            {
                return (true, $"验证成功！接口返回正常 (译文: {result.Replace("\n", " ")})");
            }
            return (false, "验证失败：接口未返回有效翻译结果，请核对 App ID 与密钥。");
        }
        catch (Exception ex)
        {
            return (false, $"验证失败：{ex.Message}");
        }
    }

    private static string Md5Hex(string input)
    {
        using var md5 = MD5.Create();
        byte[] bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(input));
        var sb = new StringBuilder();
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}
