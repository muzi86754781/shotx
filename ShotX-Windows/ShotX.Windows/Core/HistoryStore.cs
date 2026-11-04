using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Text.Json;

namespace ShotX.Windows.Core;

public class HistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FileName { get; set; } = "";
    public string FilePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public int Width { get; set; }
    public int Height { get; set; }
    public string Kind { get; set; } = "截图"; // 截图 / 长截图 / 录屏 / 录音 / OCR
    public string TextContent { get; set; } = "";
}

public static class HistoryStore
{
    private static readonly string StorageDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX", "Captures");
    private static readonly string IndexFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX", "history_index.json");

    private static readonly List<HistoryEntry> _entries = new();
    private static readonly object _lock = new();

    public static IReadOnlyList<HistoryEntry> Entries
    {
        get
        {
            lock (_lock)
            {
                return _entries.ToArray();
            }
        }
    }

    static HistoryStore()
    {
        Load();
    }

    public static void Load()
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(StorageDir))
                {
                    Directory.CreateDirectory(StorageDir);
                }

                if (File.Exists(IndexFile))
                {
                    string json = File.ReadAllText(IndexFile);
                    var list = JsonSerializer.Deserialize<List<HistoryEntry>>(json);
                    if (list != null)
                    {
                        _entries.Clear();
                        _entries.AddRange(list);
                    }
                }
            }
            catch
            {
            }
        }
    }

    public static void Save()
    {
        lock (_lock)
        {
            try
            {
                string dir = Path.GetDirectoryName(IndexFile)!;
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                string json = JsonSerializer.Serialize(_entries, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(IndexFile, json);
            }
            catch
            {
            }
        }
    }

    public static HistoryEntry AddCapture(Bitmap bmp, string kind = "截图")
    {
        lock (_lock)
        {
            try
            {
                if (!Directory.Exists(StorageDir)) Directory.CreateDirectory(StorageDir);

                string fileName = $"ShotX_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString()[..6]}.png";
                string fullPath = Path.Combine(StorageDir, fileName);

                bmp.Save(fullPath, ImageFormat.Png);

                var entry = new HistoryEntry
                {
                    FileName = fileName,
                    FilePath = fullPath,
                    CreatedAt = DateTime.Now,
                    Width = bmp.Width,
                    Height = bmp.Height,
                    Kind = kind
                };

                _entries.Insert(0, entry);
                if (_entries.Count > 100)
                {
                    _entries.RemoveAt(_entries.Count - 1);
                }

                Save();
                return entry;
            }
            catch
            {
                return new HistoryEntry();
            }
        }
    }

    public static void AddOcr(string text)
    {
        lock (_lock)
        {
            try
            {
                var entry = new HistoryEntry
                {
                    FileName = "提取文字",
                    FilePath = "",
                    CreatedAt = DateTime.Now,
                    Kind = "OCR",
                    TextContent = text
                };

                _entries.Insert(0, entry);
                if (_entries.Count > 100) _entries.RemoveAt(_entries.Count - 1);
                Save();
            }
            catch
            {
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
                _entries.RemoveAll(e => e.Id == entry.Id);
                Save();
            }
            catch
            {
            }
        }
    }

    public static void Clear()
    {
        lock (_lock)
        {
            try
            {
                foreach (var e in _entries)
                {
                    if (!string.IsNullOrEmpty(e.FilePath) && File.Exists(e.FilePath))
                    {
                        try { File.Delete(e.FilePath); } catch { }
                    }
                }
                _entries.Clear();
                Save();
            }
            catch
            {
            }
        }
    }
}
