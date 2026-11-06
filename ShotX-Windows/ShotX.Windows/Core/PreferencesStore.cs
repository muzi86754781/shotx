using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ShotX.Windows.Core;

public class AppPreferences
{
    public string SavePath { get; set; } = PreferencesStore.DefaultDownloadsDir;
    public string RecordPath { get; set; } = PreferencesStore.DefaultDownloadsDir;
    public string AudioPath { get; set; } = PreferencesStore.DefaultDownloadsDir;
    public string ImageFormat { get; set; } = "PNG";
    public int CornerRadius { get; set; } = 0;
    public bool IncludeShadow { get; set; } = false;
    public bool PlaySound { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = false;

    public string ScreenshotShortcut { get; set; } = "Alt+A";
    public string ScrollingShortcut { get; set; } = "Alt+S";
    public string ClipboardShortcut { get; set; } = "Alt+C";
    public string TranslationShortcut { get; set; } = "Alt+V";

    public Dictionary<string, string> Hotkeys { get; set; } = PreferencesStore.CreateDefaultHotkeys();
}

public static class PreferencesStore
{
    public static readonly string DefaultDownloadsDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Downloads"
    );

    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX");
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    private static AppPreferences? _current;

    public static Dictionary<string, string> CreateDefaultHotkeys() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["AllInOne"] = "未设置",
        ["Area"] = "Alt + A",
        ["PreviousArea"] = "Alt + R",
        ["Fullscreen"] = "Alt + Shift + A",
        ["Window"] = "Alt + W",
        ["OCR"] = "未设置",
        ["Pin"] = "Alt + F3",
        ["EditClipboard"] = "Alt + P",
        ["ColorPicker"] = "Alt + Shift + C",
        ["ScreenRecord"] = "Alt + Shift + R",
        ["Translation"] = "Alt + V",
        ["ClipboardHistory"] = "Alt + C"
    };

    public static AppPreferences Current
    {
        get
        {
            if (_current == null)
            {
                Load();
            }
            return _current!;
        }
    }

    public static bool AutoStart
    {
        get => Current.LaunchAtStartup;
        set => SetLaunchAtStartup(value);
    }

    public static string SaveDir
    {
        get => Current.SavePath;
        set => Current.SavePath = value;
    }

    public static string RecordDir
    {
        get => Current.RecordPath;
        set => Current.RecordPath = value;
    }

    public static string AudioDir
    {
        get => Current.AudioPath;
        set => Current.AudioPath = value;
    }

    public static string ImageFormat
    {
        get => Current.ImageFormat;
        set => Current.ImageFormat = value;
    }

    public static string GetHotkey(string actionKey)
    {
        EnsureHotkeysInitialized();
        if (Current.Hotkeys.TryGetValue(actionKey, out string? val) && !string.IsNullOrWhiteSpace(val))
        {
            return val;
        }
        return "未设置";
    }

    public static void SetHotkey(string actionKey, string shortcutText)
    {
        EnsureHotkeysInitialized();
        Current.Hotkeys[actionKey] = string.IsNullOrWhiteSpace(shortcutText) ? "未设置" : shortcutText;
        Save();
    }

    public static void ClearSingleHotkey(string actionKey)
    {
        EnsureHotkeysInitialized();
        Current.Hotkeys[actionKey] = "未设置";
        Save();
    }

    public static void ClearNonCoreHotkeys()
    {
        EnsureHotkeysInitialized();
        var keys = new List<string>(Current.Hotkeys.Keys);
        foreach (var k in keys)
        {
            Current.Hotkeys[k] = string.Equals(k, "Area", StringComparison.OrdinalIgnoreCase)
                ? "Alt + A"
                : "未设置";
        }
        Save();
    }

    public static void RestoreDefaultHotkeys()
    {
        Current.Hotkeys = CreateDefaultHotkeys();
        Save();
    }

    private static void EnsureHotkeysInitialized()
    {
        if (Current.Hotkeys == null || Current.Hotkeys.Count == 0)
        {
            Current.Hotkeys = CreateDefaultHotkeys();
            return;
        }

        var defaults = CreateDefaultHotkeys();
        foreach (var kv in defaults)
        {
            if (!Current.Hotkeys.ContainsKey(kv.Key))
            {
                Current.Hotkeys[kv.Key] = kv.Value;
            }
        }
    }

    public static string GetValidSaveDir() => EnsureDirectory(Current.SavePath);
    public static string GetValidRecordDir() => EnsureDirectory(Current.RecordPath);
    public static string GetValidAudioDir() => EnsureDirectory(Current.AudioPath);

    private static string EnsureDirectory(string? configuredPath)
    {
        string target = string.IsNullOrWhiteSpace(configuredPath) ? DefaultDownloadsDir : configuredPath;
        try
        {
            if (!Directory.Exists(target))
            {
                Directory.CreateDirectory(target);
            }
            return target;
        }
        catch
        {
            try
            {
                if (!Directory.Exists(DefaultDownloadsDir))
                {
                    Directory.CreateDirectory(DefaultDownloadsDir);
                }
            }
            catch { }
            return DefaultDownloadsDir;
        }
    }

    private static bool ShouldMigrateLegacyPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        if (path.EndsWith(@"Pictures\ShotX", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.EndsWith(@"ShotX\Captures", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                string json = File.ReadAllText(ConfigFile);
                _current = JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();

                bool migrated = false;
                if (ShouldMigrateLegacyPath(_current.SavePath))
                {
                    _current.SavePath = DefaultDownloadsDir;
                    migrated = true;
                }
                if (ShouldMigrateLegacyPath(_current.RecordPath))
                {
                    _current.RecordPath = DefaultDownloadsDir;
                    migrated = true;
                }
                if (ShouldMigrateLegacyPath(_current.AudioPath))
                {
                    _current.AudioPath = DefaultDownloadsDir;
                    migrated = true;
                }
                if (_current.Hotkeys == null || _current.Hotkeys.Count == 0)
                {
                    _current.Hotkeys = CreateDefaultHotkeys();
                    migrated = true;
                }
                if (migrated)
                {
                    Save();
                }
            }
            else
            {
                _current = new AppPreferences();
                Save();
            }
        }
        catch
        {
            _current = new AppPreferences();
        }
    }

    public static void Save()
    {
        try
        {
            if (!Directory.Exists(ConfigDir))
            {
                Directory.CreateDirectory(ConfigDir);
            }
            string json = JsonSerializer.Serialize(Current, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(ConfigFile, json);
        }
        catch
        {
        }
    }

    public static void SetLaunchAtStartup(bool enable)
    {
        Current.LaunchAtStartup = enable;
        Save();

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key != null)
            {
                string appPath = Environment.ProcessPath ?? "";
                if (enable && !string.IsNullOrEmpty(appPath))
                {
                    key.SetValue("ShotX", $"\"{appPath}\"");
                }
                else
                {
                    key.DeleteValue("ShotX", false);
                }
            }
        }
        catch
        {
        }
    }
}
