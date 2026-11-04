using System;
using System.IO;
using System.Text.Json;
using Microsoft.Win32;

namespace ShotX.Windows.Core;

public class AppPreferences
{
    public string SavePath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string RecordPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string AudioPath { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    public string ImageFormat { get; set; } = "PNG";
    public int CornerRadius { get; set; } = 0;
    public bool IncludeShadow { get; set; } = false;
    public bool PlaySound { get; set; } = true;
    public bool LaunchAtStartup { get; set; } = false;

    public string ScreenshotShortcut { get; set; } = "Alt+A";
    public string ScrollingShortcut { get; set; } = "Alt+S";
    public string ClipboardShortcut { get; set; } = "Alt+C";
    public string TranslationShortcut { get; set; } = "Alt+V";
}

public static class PreferencesStore
{
    private static readonly string ConfigDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ShotX");
    private static readonly string ConfigFile = Path.Combine(ConfigDir, "config.json");

    private static AppPreferences? _current;

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

    public static void Load()
    {
        try
        {
            if (File.Exists(ConfigFile))
            {
                string json = File.ReadAllText(ConfigFile);
                _current = JsonSerializer.Deserialize<AppPreferences>(json) ?? new AppPreferences();
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
