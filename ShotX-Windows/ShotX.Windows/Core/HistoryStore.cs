using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ShotX.Windows.Core;

public enum HistoryType
{
    Screenshot,
    ScreenRecord,
    AudioRecord,
    OCR,
    Translation,
    Clipboard
}

public class HistoryItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public HistoryType Type { get; set; } = HistoryType.Screenshot;
    public string Content { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TimestampString => Timestamp.ToString("yyyy-MM-dd HH:mm:ss");
    public string Icon => Type switch
    {
        HistoryType.Screenshot => "🖼️",
        HistoryType.ScreenRecord => "📹",
        HistoryType.AudioRecord => "🎙️",
        HistoryType.OCR => "🔤",
        HistoryType.Translation => "🌐",
        HistoryType.Clipboard => "📋",
        _ => "📄"
    };

    public string TypeBadgeText => Type switch
    {
        HistoryType.Screenshot => "截图",
        HistoryType.ScreenRecord => "录屏",
        HistoryType.AudioRecord => "录音",
        HistoryType.OCR => "提取文字",
        HistoryType.Translation => "翻译",
        HistoryType.Clipboard => "剪贴板",
        _ => "记录"
    };

    public string BadgeBackground => Type switch
    {
        HistoryType.Screenshot => "#E0F2FE",
        HistoryType.ScreenRecord => "#FEE2E2",
        HistoryType.AudioRecord => "#FEF3C7",
        HistoryType.OCR => "#DCFCE7",
        HistoryType.Translation => "#F3E8FF",
        HistoryType.Clipboard => "#F1F5F9",
        _ => "#F1F5F9"
    };

    public string BadgeForeground => Type switch
    {
        HistoryType.Screenshot => "#0284C7",
        HistoryType.ScreenRecord => "#DC2626",
        HistoryType.AudioRecord => "#D97706",
        HistoryType.OCR => "#16A34A",
        HistoryType.Translation => "#7C3AED",
        HistoryType.Clipboard => "#475569",
        _ => "#475569"
    };

    public bool HasFile => !string.IsNullOrWhiteSpace(FilePath) && File.Exists(FilePath);

    public bool HasImageThumbnail => !string.IsNullOrWhiteSpace(FilePath) &&
                                     File.Exists(FilePath) &&
                                     (FilePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                      FilePath.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                      FilePath.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                      FilePath.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase));

    public bool IsMedia => Type == HistoryType.ScreenRecord;

    public bool IsAudio => Type == HistoryType.AudioRecord;

    public bool IsTranslation => Type == HistoryType.Translation;

    public System.Windows.Visibility ImageThumbnailVisibility =>
        HasImageThumbnail ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility PlaceholderBadgeVisibility =>
        HasImageThumbnail ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public System.Windows.Visibility MediaBadgeVisibility =>
        IsMedia ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility TranslationCardVisibility =>
        IsTranslation ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public System.Windows.Visibility StandardCardVisibility =>
        IsTranslation ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;

    public string TranslationSource
    {
        get
        {
            if (Type != HistoryType.Translation || string.IsNullOrEmpty(Content)) return Content ?? "";
            int idx = Content.IndexOf("  ➔  ", StringComparison.Ordinal);
            if (idx >= 0) return Content[..idx].Trim();
            int idx2 = Content.IndexOf(" -> ", StringComparison.Ordinal);
            if (idx2 >= 0) return Content[..idx2].Trim();
            return Content;
        }
    }

    public string TranslationTarget
    {
        get
        {
            if (Type != HistoryType.Translation || string.IsNullOrEmpty(Content)) return "";
            int idx = Content.IndexOf("  ➔  ", StringComparison.Ordinal);
            if (idx >= 0) return Content[(idx + 5)..].Trim();
            int idx2 = Content.IndexOf(" -> ", StringComparison.Ordinal);
            if (idx2 >= 0) return Content[(idx2 + 4)..].Trim();
            return "";
        }
    }

    public System.Windows.Visibility FileActionVisibility =>
        HasFile ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;

    public string DisplayTitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Content))
            {
                string oneLine = Content.Replace("\r\n", " ").Replace("\n", " ").Trim();
                return oneLine.Length > 90 ? oneLine[..90] + "..." : oneLine;
            }
            if (!string.IsNullOrWhiteSpace(FilePath))
            {
                return Path.GetFileName(FilePath);
            }
            return TypeBadgeText;
        }
    }

    public string DisplaySubtitle
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(FilePath))
            {
                return $"文件路径: {FilePath}";
            }
            string clean = (Content ?? "").Replace("\r\n", "  ").Replace("\n", "  ").Trim();
            return clean.Length > 140 ? clean[..140] + "..." : clean;
        }
    }
}

public class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Kind { get; set; } = "截图";
    public string TextContent { get; set; } = "";
}

public static class HistoryStore
{
    private static readonly string StorageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX", "Captures");
    private static readonly string IndexFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX", "history_items.json");

    private static readonly List<HistoryItem> _items = new();
    private static readonly object _lock = new();

    static HistoryStore()
    {
        Load();
    }

    private static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(IndexFile))
                {
                    string json = File.ReadAllText(IndexFile);
                    var list = JsonSerializer.Deserialize<List<HistoryItem>>(json);
                    if (list != null)
                    {
                        _items.Clear();
                        _items.AddRange(list);
                    }
                }
            }
            catch { }
        }
    }

    private static void Save()
    {
        lock (_lock)
        {
            try
            {
                string dir = Path.GetDirectoryName(IndexFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string json = JsonSerializer.Serialize(_items, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(IndexFile, json);
            }
            catch { }
        }
    }

    public static IReadOnlyList<HistoryItem> GetAll()
    {
        lock (_lock)
        {
            return _items.OrderByDescending(i => i.Timestamp).ToList();
        }
    }

    public static List<HistoryItem> GetItemsByType(HistoryType type)
    {
        lock (_lock)
        {
            return _items.Where(i => i.Type == type).OrderByDescending(i => i.Timestamp).ToList();
        }
    }

    public static void Add(HistoryType type, string filePathOrText, string content)
    {
        lock (_lock)
        {
            try
            {
                bool isFile = !string.IsNullOrWhiteSpace(filePathOrText) && File.Exists(filePathOrText);
                string finalContent = string.IsNullOrEmpty(content) ? filePathOrText : content;
                if (type == HistoryType.Translation && !isFile && !string.IsNullOrWhiteSpace(filePathOrText) && filePathOrText != content)
                {
                    finalContent = $"{filePathOrText.Trim()}  ➔  {content.Trim()}";
                }

                // 剪贴板或翻译连续重复内容自动去重更新时间
                if ((type == HistoryType.Clipboard || type == HistoryType.Translation) &&
                    _items.Count > 0 &&
                    _items[0].Type == type &&
                    _items[0].Content == finalContent)
                {
                    _items[0].Timestamp = DateTime.Now;
                    Save();
                    return;
                }

                var item = new HistoryItem
                {
                    Type = type,
                    FilePath = isFile ? filePathOrText : "",
                    Content = finalContent,
                    Timestamp = DateTime.Now
                };
                _items.Insert(0, item);
                if (_items.Count > 300) _items.RemoveAt(_items.Count - 1);
                Save();
            }
            catch { }
        }
    }

    public static void Delete(HistoryItem item)
    {
        lock (_lock)
        {
            try
            {
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    try { File.Delete(item.FilePath); } catch { }
                }
                int removed = _items.RemoveAll(i => i.Id == item.Id);
                if (removed == 0)
                {
                    _items.RemoveAll(i => i.Type == item.Type && i.Content == item.Content && i.Timestamp == item.Timestamp);
                }
                Save();
            }
            catch { }
        }
    }

    public static void Clear(HistoryType? type = null)
    {
        lock (_lock)
        {
            try
            {
                if (type.HasValue)
                {
                    _items.RemoveAll(i => i.Type == type.Value);
                }
                else
                {
                    _items.Clear();
                }
                Save();
            }
            catch { }
        }
    }

    public static IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _items.Select(i => new HistoryEntry
                {
                    Id = i.Id,
                    FileName = Path.GetFileName(i.FilePath),
                    FilePath = i.FilePath,
                    CreatedAt = i.Timestamp,
                    Kind = i.Type switch
                    {
                        HistoryType.ScreenRecord => "录屏",
                        HistoryType.AudioRecord => "录音",
                        HistoryType.OCR => "OCR",
                        HistoryType.Translation => "翻译",
                        HistoryType.Clipboard => "剪贴板",
                        _ => "截图"
                    },
                    TextContent = i.Content
                }).ToList();
            }
        }
    }

    public static void Delete(HistoryEntry entry)
    {
        lock (_lock)
        {
            try
            {
                if (!string.IsNullOrEmpty(entry.FilePath) && File.Exists(entry.FilePath))
                {
                    File.Delete(entry.FilePath);
                }
                _items.RemoveAll(i => i.Id == entry.Id);
                Save();
            }
            catch { }
        }
    }

    // 兼容历史老接口
    public static void Add(Bitmap bitmap, string kind = "截图")
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(StorageDir)) Directory.CreateDirectory(StorageDir);
                string fileName = $"ShotX_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..6]}.png";
                string fullPath = Path.Combine(StorageDir, fileName);
                bitmap.Save(fullPath, ImageFormat.Png);

                HistoryType t = kind switch
                {
                    "录屏" => HistoryType.ScreenRecord,
                    "录音" => HistoryType.AudioRecord,
                    "OCR" => HistoryType.OCR,
                    _ => HistoryType.Screenshot
                };
                Add(t, fullPath, $"{kind} - {bitmap.Width}×{bitmap.Height}");
            }
            catch { }
        }
    }

    public static void AddText(string text)
    {
        Add(HistoryType.OCR, text, text);
    }

    public static void AddOcr(string text)
    {
        Add(HistoryType.OCR, text, text);
    }

    public static void AddCapture(Bitmap bitmap, string kind = "截图")
    {
        Add(bitmap, kind);
    }
}
