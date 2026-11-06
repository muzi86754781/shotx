using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ShotX.Windows.Core;
using ShotX.Windows.Models;
using ShotX.Windows.Views;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows;

public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private NotifyIcon? _notifyIcon;
    private HotKeyManager? _hotKeyManager;
    private OverlayWindow? _currentOverlay;
    private PreferencesWindow? _preferencesWindow;
    private TranslationWindow? _translationWindow;
    private ClipboardHistoryWindow? _clipboardHistoryWindow;

    public static IntPtr LastExternalForegroundHwnd { get; private set; } = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private const byte VK_SHIFT = 0x10;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12; // Alt
    private const byte VK_C = 0x43;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    protected override void OnStartup(StartupEventArgs e)
    {
        // 全局未捕获异常防护，彻底杜绝任何子窗口关闭或事件重入导致软件闪退
        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
        };

        base.OnStartup(e);
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // 1. 单实例互斥检查
        _singleInstanceMutex = new Mutex(true, "ShotX_Windows_SingleInstance_Mutex", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("ShotX 已经在运行中，请按快捷键 Alt+A 呼出截图或检查右下角系统托盘。", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 2. 初始化偏好配置 (自动确保默认保存目录为 Downloads)
        PreferencesStore.Load();

        // 3. 初始化系统托盘图标
        InitNotifyIcon();

        // 4. 初始化全局快捷键
        InitGlobalHotkeys();

        // 5. 启动后台剪贴板与外部焦点窗口监听
        StartClipboardWatcher();
        StartExternalWindowTracker();
    }

    private void StartExternalWindowTracker()
    {
        uint currentPid = (uint)Environment.ProcessId;
        var trackerTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(220)
        };
        trackerTimer.Tick += (_, _) =>
        {
            try
            {
                IntPtr fg = GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                GetWindowThreadProcessId(fg, out uint pid);
                if (pid == currentPid) return;

                var sb = new StringBuilder(64);
                GetClassName(fg, sb, sb.Capacity);
                string cls = sb.ToString();
                if (cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase) ||
                    cls.Equals("Progman", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                LastExternalForegroundHwnd = fg;
            }
            catch
            {
            }
        };
        trackerTimer.Start();
    }

    private static Icon LoadTrayIcon()
    {
        try
        {
            string icoPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "app.ico");
            if (File.Exists(icoPath))
            {
                return new Icon(icoPath);
            }
            if (!string.IsNullOrEmpty(Environment.ProcessPath) && File.Exists(Environment.ProcessPath))
            {
                var extracted = Icon.ExtractAssociatedIcon(Environment.ProcessPath);
                if (extracted != null) return extracted;
            }
        }
        catch
        {
        }
        return SystemIcons.Application;
    }

    private void InitNotifyIcon()
    {
        var contextMenu = new ContextMenuStrip
        {
            Renderer = new TrayMenuHelper.ModernMenuRenderer(),
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9.25f, System.Drawing.FontStyle.Regular),
            ShowImageMargin = true,
            ShowCheckMargin = false,
            AutoClose = true
        };

        var textColor = TrayMenuHelper.TextColor;
        var hotkeys = PreferencesStore.Current.Hotkeys;

        // 1. 全能截图 (macOS 1:1 首项高亮风格)
        var heroItem = new ToolStripMenuItem("全能截图", TrayMenuHelper.CreateIcon("all_in_one", System.Drawing.Color.White, 16), (_, _) => Dispatcher.BeginInvoke(ShowAllInOneBar))
        {
            Tag = "Hero",
            Font = new System.Drawing.Font("Microsoft YaHei UI", 9.5f, System.Drawing.FontStyle.Bold)
        };
        contextMenu.Items.Add(heroItem);

        // 2. 区域截图
        var itemArea = new ToolStripMenuItem("区域截图", TrayMenuHelper.CreateIcon("area", textColor), (_, _) => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.Area)))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("Area", "Alt + A")
        };
        contextMenu.Items.Add(itemArea);

        // 3. 上次区域截图
        var itemPrev = new ToolStripMenuItem("上次区域截图", TrayMenuHelper.CreateIcon("prev_area", textColor), (_, _) => Dispatcher.BeginInvoke(CapturePreviousArea))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("PreviousArea", "Alt + R")
        };
        contextMenu.Opening += (_, _) => itemPrev.Enabled = LastCaptureStore.HasPrevious;
        contextMenu.Items.Add(itemPrev);

        // 4. 全屏截图
        var itemFull = new ToolStripMenuItem("全屏截图", TrayMenuHelper.CreateIcon("fullscreen", textColor), (_, _) => Dispatcher.BeginInvoke(CaptureFullscreen))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("Fullscreen", "Alt + Shift + A")
        };
        contextMenu.Items.Add(itemFull);

        // 5. 窗口截图
        var itemWin = new ToolStripMenuItem("窗口截图", TrayMenuHelper.CreateIcon("window", textColor), (_, _) => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.Window)))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("Window", "Alt + W")
        };
        contextMenu.Items.Add(itemWin);

        // 6. 定时截图
        var timerMenu = new ToolStripMenuItem("定时截图", TrayMenuHelper.CreateIcon("timer", textColor));
        timerMenu.DropDownItems.Add("3 秒", null, (_, _) => Dispatcher.BeginInvoke(() => StartTimerCapture(3)));
        timerMenu.DropDownItems.Add("5 秒", null, (_, _) => Dispatcher.BeginInvoke(() => StartTimerCapture(5)));
        timerMenu.DropDownItems.Add("10 秒", null, (_, _) => Dispatcher.BeginInvoke(() => StartTimerCapture(10)));
        contextMenu.Items.Add(timerMenu);

        // 7. 截图 OCR
        var itemOcr = new ToolStripMenuItem("截图 OCR", TrayMenuHelper.CreateIcon("ocr", textColor), (_, _) => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.OCR)))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("OCR", "")
        };
        contextMenu.Items.Add(itemOcr);

        // 8. 从剪贴板贴图
        var itemPin = new ToolStripMenuItem("从剪贴板贴图", TrayMenuHelper.CreateIcon("pin", textColor), (_, _) => Dispatcher.BeginInvoke(PinFromClipboard))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("Pin", "Alt + F3")
        };
        contextMenu.Items.Add(itemPin);

        // 9. 改图-打开图片 (直接进入可编辑标注模式)
        var itemOpenImg = new ToolStripMenuItem("改图-打开图片", TrayMenuHelper.CreateIcon("open_image", textColor), (_, _) => Dispatcher.BeginInvoke(OpenFileForEdit));
        contextMenu.Items.Add(itemOpenImg);

        // 10. 改图-从剪贴板打开 (直接进入可编辑标注模式)
        var itemEditClip = new ToolStripMenuItem("改图-从剪贴板打开", TrayMenuHelper.CreateIcon("edit_clipboard", textColor), (_, _) => Dispatcher.BeginInvoke(EditFromClipboard))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("EditClipboard", "Alt + P")
        };
        contextMenu.Items.Add(itemEditClip);

        // 11. 拾色器
        var itemColor = new ToolStripMenuItem("拾色器", TrayMenuHelper.CreateIcon("color_picker", textColor), (_, _) => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.ColorPicker)))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("ColorPicker", "Alt + Shift + C")
        };
        contextMenu.Items.Add(itemColor);

        contextMenu.Items.Add(new ToolStripSeparator());

        // 12. 屏幕录制
        var itemRec = new ToolStripMenuItem("屏幕录制", TrayMenuHelper.CreateIcon("screen_record", textColor), (_, _) => Dispatcher.BeginInvoke(StartScreenRecording))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("ScreenRecord", "Alt + Shift + R")
        };
        contextMenu.Items.Add(itemRec);

        // 13. 声音录制
        var itemAudio = new ToolStripMenuItem("声音录制", TrayMenuHelper.CreateIcon("audio_record", textColor), (_, _) => Dispatcher.BeginInvoke(StartAudioRecording));
        contextMenu.Items.Add(itemAudio);

        contextMenu.Items.Add(new ToolStripSeparator());

        // 14. 划词翻译
        var itemTransSel = new ToolStripMenuItem("划词翻译", TrayMenuHelper.CreateIcon("translate_selection", textColor), (_, _) => Dispatcher.BeginInvoke(TranslateSelection))
        {
            ShortcutKeyDisplayString = "Alt + Shift + F"
        };
        contextMenu.Items.Add(itemTransSel);

        // 15. 截图翻译
        var itemTransShot = new ToolStripMenuItem("截图翻译", TrayMenuHelper.CreateIcon("translate_screenshot", textColor), (_, _) => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.Translate)));
        contextMenu.Items.Add(itemTransShot);

        // 16. 输入翻译
        var itemTransWin = new ToolStripMenuItem("输入翻译", TrayMenuHelper.CreateIcon("translate_input", textColor), (_, _) => Dispatcher.BeginInvoke(ShowTranslationWindow))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("Translation", "Alt + V")
        };
        contextMenu.Items.Add(itemTransWin);

        contextMenu.Items.Add(new ToolStripSeparator());

        // 17. 剪贴板历史
        var itemClipHist = new ToolStripMenuItem("剪贴板历史", TrayMenuHelper.CreateIcon("clipboard_history", textColor), (_, _) => Dispatcher.BeginInvoke(ShowClipboardHistoryWindow))
        {
            ShortcutKeyDisplayString = hotkeys.GetValueOrDefault("ClipboardHistory", "Alt + C")
        };
        contextMenu.Items.Add(itemClipHist);

        contextMenu.Items.Add(new ToolStripSeparator());

        // 18. 隐藏/显示桌面图标
        bool isDesktopVisible = DesktopIcons.IsVisible();
        var itemDesktop = new ToolStripMenuItem(
            isDesktopVisible ? "隐藏桌面图标" : "显示桌面图标",
            TrayMenuHelper.CreateIcon(isDesktopVisible ? "desktop_icons_slash" : "desktop_icons", textColor),
            (_, _) => ToggleDesktopIcons()
        );
        contextMenu.Opening += (_, _) =>
        {
            bool visible = DesktopIcons.IsVisible();
            itemDesktop.Text = visible ? "隐藏桌面图标" : "显示桌面图标";
            itemDesktop.Image = TrayMenuHelper.CreateIcon(visible ? "desktop_icons_slash" : "desktop_icons", textColor);
        };
        contextMenu.Items.Add(itemDesktop);

        // 19. 历史记录
        var itemHistory = new ToolStripMenuItem("历史记录", TrayMenuHelper.CreateIcon("history", textColor), (_, _) => Dispatcher.BeginInvoke(() => ShowPreferences(initialTabIsHistory: true)));
        contextMenu.Items.Add(itemHistory);

        contextMenu.Items.Add(new ToolStripSeparator());

        // 20. 支持作者
        var itemSupport = new ToolStripMenuItem("支持作者", TrayMenuHelper.CreateIcon("support_author", textColor), (_, _) => Dispatcher.BeginInvoke(ShowSupportAuthorWindow));
        contextMenu.Items.Add(itemSupport);

        // 21. 偏好设置
        var itemPrefs = new ToolStripMenuItem("偏好设置", TrayMenuHelper.CreateIcon("preferences", textColor), (_, _) => Dispatcher.BeginInvoke(() => ShowPreferences(initialTabIsHistory: false)));
        contextMenu.Items.Add(itemPrefs);

        // 22. 检查更新
        var itemUpdates = new ToolStripMenuItem("检查更新", TrayMenuHelper.CreateIcon("check_update", textColor), (_, _) => Dispatcher.BeginInvoke(CheckForUpdates));
        contextMenu.Items.Add(itemUpdates);

        // 23. 关于 ShotX
        var itemAbout = new ToolStripMenuItem("关于 ShotX", TrayMenuHelper.CreateIcon("about", textColor), (_, _) => Dispatcher.BeginInvoke(() => ShowPreferences(initialTabIsHistory: false)));
        contextMenu.Items.Add(itemAbout);

        // 24. 退出 ShotX
        var itemExit = new ToolStripMenuItem("退出 ShotX", TrayMenuHelper.CreateIcon("exit", textColor), (_, _) => ExitApp());
        contextMenu.Items.Add(itemExit);

        _notifyIcon = new NotifyIcon
        {
            Text = "ShotX - 全能屏幕截图与录制神器",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.Area));
    }

    public void ReloadGlobalHotkeys()
    {
        try
        {
            _hotKeyManager?.Dispose();
            _hotKeyManager = null;
        }
        catch { }
        InitGlobalHotkeys();
    }

    private void InitGlobalHotkeys()
    {
        try
        {
            _hotKeyManager = new HotKeyManager();

            RegisterConfiguredHotkey("AllInOne", () => Dispatcher.BeginInvoke(ShowAllInOneBar));
            RegisterConfiguredHotkey("Area", () => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.Area)));
            RegisterConfiguredHotkey("PreviousArea", () => Dispatcher.BeginInvoke(CapturePreviousArea));
            RegisterConfiguredHotkey("Fullscreen", () => Dispatcher.BeginInvoke(CaptureFullscreen));
            RegisterConfiguredHotkey("Window", () => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.Window)));
            RegisterConfiguredHotkey("OCR", () => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.OCR)));
            RegisterConfiguredHotkey("Pin", () => Dispatcher.BeginInvoke(PinFromClipboard));
            RegisterConfiguredHotkey("EditClipboard", () => Dispatcher.BeginInvoke(EditFromClipboard));
            RegisterConfiguredHotkey("ColorPicker", () => Dispatcher.BeginInvoke(() => StartScreenshot(OverlayMode.ColorPicker)));
            RegisterConfiguredHotkey("ScreenRecord", () => Dispatcher.BeginInvoke(StartScreenRecording));
            RegisterConfiguredHotkey("Translation", () => Dispatcher.BeginInvoke(ShowTranslationWindow));
            RegisterConfiguredHotkey("ClipboardHistory", () => Dispatcher.BeginInvoke(ShowClipboardHistoryWindow));

            // 额外保持 Alt + Shift + F 划词翻译热键
            _hotKeyManager.Register(ModifierKeys.Alt | ModifierKeys.Shift, Key.F, () => Dispatcher.BeginInvoke(TranslateSelection));
        }
        catch
        {
        }
    }

    private void RegisterConfiguredHotkey(string actionKey, Action callback)
    {
        if (_hotKeyManager == null) return;
        string text = PreferencesStore.GetHotkey(actionKey);
        if (TryParseShortcut(text, out var mods, out var key))
        {
            try
            {
                _hotKeyManager.Register(mods, key, callback);
            }
            catch { }
        }
    }

    public static bool TryParseShortcut(string? shortcutText, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;

        if (string.IsNullOrWhiteSpace(shortcutText) || shortcutText.Contains("未设置"))
        {
            return false;
        }

        string[] parts = shortcutText.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var part in parts)
        {
            if (part.Equals("Alt", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Alt;
            }
            else if (part.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) || part.Equals("Control", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Control;
            }
            else if (part.Equals("Shift", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Shift;
            }
            else if (part.Equals("Win", StringComparison.OrdinalIgnoreCase) || part.Equals("Windows", StringComparison.OrdinalIgnoreCase))
            {
                modifiers |= ModifierKeys.Windows;
            }
            else if (Enum.TryParse<Key>(part, true, out var parsedKey))
            {
                key = parsedKey;
            }
        }

        return key != Key.None;
    }

    private void StartClipboardWatcher()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.2)
        };
        string lastText = "";
        timer.Tick += (_, _) =>
        {
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    string txt = System.Windows.Clipboard.GetText().Trim();
                    if (!string.IsNullOrEmpty(txt) && txt != lastText)
                    {
                        lastText = txt;
                        HistoryStore.Add(HistoryType.Clipboard, txt, txt);
                    }
                }
            }
            catch
            {
            }
        };
        timer.Start();
    }

    public void StartScreenshot(OverlayMode mode = OverlayMode.Area)
    {
        try
        {
            if (_currentOverlay != null)
            {
                try { _currentOverlay.Close(); } catch { }
                _currentOverlay = null;
            }

            _currentOverlay = new OverlayWindow(mode);
            _currentOverlay.Closed += (_, _) => _currentOverlay = null;
            _currentOverlay.Show();
        }
        catch
        {
        }
    }

    public void StartTimerCapture(int seconds)
    {
        var win = new CountdownWindow(seconds, () => StartScreenshot(OverlayMode.Area));
        win.Show();
    }

    public void ShowAllInOneBar()
    {
        var bar = new AllInOneBarWindow();
        bar.Show();
    }

    /// <summary>
    /// 打开或激活设置中心单实例，支持直接定位到历史记录对应分类 (解决需求 6)
    /// </summary>
    public void ShowPreferences(bool initialTabIsHistory = false, HistoryType? filterType = null)
    {
        if (_preferencesWindow != null && _preferencesWindow.IsLoaded)
        {
            if (_preferencesWindow.WindowState == WindowState.Minimized)
            {
                _preferencesWindow.WindowState = WindowState.Normal;
            }
            _preferencesWindow.NavigateTo(initialTabIsHistory, filterType);
            _preferencesWindow.Topmost = true;
            _preferencesWindow.Activate();
            _preferencesWindow.Topmost = false;
            _preferencesWindow.Focus();
            return;
        }

        _preferencesWindow = new PreferencesWindow(initialTabIsHistory, filterType);
        _preferencesWindow.Closed += (_, _) => _preferencesWindow = null;
        _preferencesWindow.Show();
        _preferencesWindow.Activate();
    }

    public void StartScreenRecording()
    {
        var prep = new ScreenRecordingPrepWindow();
        prep.Show();
    }

    public void StartAudioRecording()
    {
        var audio = new AudioRecordingWindow();
        audio.Show();
    }

    /// <summary>
    /// 划词翻译：自动恢复外部目标窗口焦点、释放修饰键并模拟 Ctrl+C 抓取选中文本放入翻译框 (解决需求 7)
    /// </summary>
    public async void TranslateSelection()
    {
        try
        {
            IntPtr targetHwnd = LastExternalForegroundHwnd;
            IntPtr currentFg = GetForegroundWindow();
            GetWindowThreadProcessId(currentFg, out uint fgPid);
            if (fgPid != (uint)Environment.ProcessId && currentFg != IntPtr.Zero)
            {
                var sb = new StringBuilder(64);
                GetClassName(currentFg, sb, sb.Capacity);
                string cls = sb.ToString();
                if (!cls.Equals("Shell_TrayWnd", StringComparison.OrdinalIgnoreCase) &&
                    !cls.Equals("NotifyIconOverflowWindow", StringComparison.OrdinalIgnoreCase))
                {
                    targetHwnd = currentFg;
                }
            }

            // 1. 若从托盘菜单点击，先将焦点还给刚才选中文字的外部窗口
            if (targetHwnd != IntPtr.Zero)
            {
                SetForegroundWindow(targetHwnd);
            }

            await Task.Delay(140);

            // 2. 释放可能仍按下的 Alt / Shift 修饰键，防止干扰 Ctrl+C
            keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            // 3. 发送 Ctrl+C 复制选中文本
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            // 4. 等待目标应用将选中文字写入系统剪贴板
            await Task.Delay(240);

            string text = "";
            try
            {
                if (System.Windows.Clipboard.ContainsText())
                {
                    text = System.Windows.Clipboard.GetText().Trim();
                }
            }
            catch { }

            OpenOrUpdateTranslationWindow(text);
        }
        catch
        {
            ShowTranslationWindow();
        }
    }

    private void OpenOrUpdateTranslationWindow(string? sourceText)
    {
        if (_translationWindow != null && _translationWindow.IsLoaded)
        {
            if (_translationWindow.WindowState == WindowState.Minimized)
            {
                _translationWindow.WindowState = WindowState.Normal;
            }
            _translationWindow.Activate();
            if (!string.IsNullOrWhiteSpace(sourceText))
            {
                _translationWindow.SetSourceText(sourceText);
            }
            return;
        }

        _translationWindow = new TranslationWindow();
        _translationWindow.Closed += (_, _) => _translationWindow = null;
        _translationWindow.Show();
        _translationWindow.Activate();
        if (!string.IsNullOrWhiteSpace(sourceText))
        {
            _translationWindow.SetSourceText(sourceText);
        }
    }

    public void ShowHistoryWindow()
    {
        ShowPreferences(initialTabIsHistory: true);
    }

    public void ShowSupportAuthorWindow()
    {
        var win = new SupportAuthorWindow();
        win.Show();
    }

    public void ShowAboutWindow()
    {
        var win = new AboutWindow();
        win.Show();
    }

    public void CheckForUpdates()
    {
        ShowAboutWindow();
    }

    public void CapturePreviousArea()
    {
        if (!LastCaptureStore.HasPrevious)
        {
            MessageBox.Show("暂无上一次截图区域记录，请先使用区域截图框选一次。", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
            StartScreenshot(OverlayMode.Area);
            return;
        }

        if (_currentOverlay != null)
        {
            try { _currentOverlay.Close(); } catch { }
            _currentOverlay = null;
        }

        _currentOverlay = new OverlayWindow(OverlayMode.Area, LastCaptureStore.LastRect);
        _currentOverlay.Closed += (_, _) => _currentOverlay = null;
        _currentOverlay.Show();
    }

    /// <summary>
    /// 全屏截图：截取当前鼠标所在显示器的全屏，自动复制到剪贴板并保存到下载目录，同时支持立即标注 (解决需求 3, 9)
    /// </summary>
    public void CaptureFullscreen()
    {
        try
        {
            var currentScreen = ScreenCapture.GetCurrentMouseScreen();
            var bounds = currentScreen.Bounds;
            var (scaleX, scaleY) = ScreenCapture.GetMonitorDpiScale(bounds);

            using (var bmp = ScreenCapture.CaptureScreen(bounds))
            {
                try
                {
                    System.Windows.Clipboard.SetImage(ScreenCapture.ToBitmapSource(bmp));
                }
                catch { }

                try
                {
                    string saveDir = PreferencesStore.GetValidSaveDir();
                    string fmt = (PreferencesStore.ImageFormat ?? "PNG").ToUpperInvariant();
                    string ext = fmt == "JPG" ? ".jpg" : ".png";
                    ImageFormat imgFmt = fmt == "JPG" ? ImageFormat.Jpeg : ImageFormat.Png;
                    string filePath = Path.Combine(saveDir, $"ShotX_Fullscreen_{DateTime.Now:yyyyMMdd_HHmmss_fff}{ext}");
                    bmp.Save(filePath, imgFmt);
                }
                catch { }

                HistoryStore.AddCapture(bmp, "全屏截图");
                if (PreferencesStore.Current.PlaySound)
                {
                    System.Media.SystemSounds.Beep.Play();
                }
            }

            if (_currentOverlay != null)
            {
                try { _currentOverlay.Close(); } catch { }
                _currentOverlay = null;
            }

            var monitorDipRect = new Rect(0, 0, bounds.Width / scaleX, bounds.Height / scaleY);
            _currentOverlay = new OverlayWindow(OverlayMode.Area, monitorDipRect);
            _currentOverlay.Closed += (_, _) => _currentOverlay = null;
            _currentOverlay.Show();
        }
        catch
        {
        }
    }

    /// <summary>
    /// 改图-打开图片：选择本地图片后进入全功能标注编辑器 (解决需求 7)
    /// </summary>
    public void OpenFileForEdit()
    {
        try
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*",
                Title = "ShotX 改图 - 选择要编辑标注的图片"
            };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                using var loaded = new Bitmap(ofd.FileName);
                var bmp = new Bitmap(loaded);

                if (_currentOverlay != null)
                {
                    try { _currentOverlay.Close(); } catch { }
                    _currentOverlay = null;
                }

                _currentOverlay = new OverlayWindow(bmp);
                _currentOverlay.Closed += (_, _) =>
                {
                    _currentOverlay = null;
                    try { bmp.Dispose(); } catch { }
                };
                _currentOverlay.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开图片失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 改图-从剪贴板打开：从剪贴板读取图片并进入全功能标注编辑器 (解决需求 7)
    /// </summary>
    public void EditFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var imgSource = System.Windows.Clipboard.GetImage();
                if (imgSource != null)
                {
                    var bmp = BitmapSourceToBitmap(imgSource);
                    if (_currentOverlay != null)
                    {
                        try { _currentOverlay.Close(); } catch { }
                        _currentOverlay = null;
                    }

                    _currentOverlay = new OverlayWindow(bmp);
                    _currentOverlay.Closed += (_, _) =>
                    {
                        _currentOverlay = null;
                        try { bmp.Dispose(); } catch { }
                    };
                    _currentOverlay.Show();
                    return;
                }
            }
            MessageBox.Show("剪贴板中暂无有效图片，请先截图或复制一张图片后再使用“改图-从剪贴板打开”。", "ShotX 改图", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取剪贴板图片失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// 从剪贴板贴图：将剪贴板图片悬浮置顶在屏幕上
    /// </summary>
    public void PinFromClipboard()
    {
        try
        {
            if (System.Windows.Clipboard.ContainsImage())
            {
                var imgSource = System.Windows.Clipboard.GetImage();
                if (imgSource != null)
                {
                    var bmp = BitmapSourceToBitmap(imgSource);
                    var pinned = new PinnedWindow(bmp, new System.Windows.Point(200, 200));
                    pinned.Show();
                    return;
                }
            }
            MessageBox.Show("剪贴板中暂无有效图片，请先复制一张图片后再贴图。", "ShotX 贴图", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取剪贴板图片失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void OpenFromClipboard() => EditFromClipboard();

    public void ToggleDesktopIcons()
    {
        DesktopIcons.Toggle();
    }

    private static Bitmap BitmapSourceToBitmap(BitmapSource source)
    {
        var formatted = new FormatConvertedBitmap(source, System.Windows.Media.PixelFormats.Bgra32, null, 0);
        var bmp = new Bitmap(formatted.PixelWidth, formatted.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        formatted.CopyPixels(Int32Rect.Empty, data.Scan0, data.Height * data.Stride, data.Stride);
        bmp.UnlockBits(data);
        return bmp;
    }

    public void ShowTranslationWindow()
    {
        OpenOrUpdateTranslationWindow(null);
    }

    public void ShowClipboardHistoryWindow()
    {
        IntPtr target = LastExternalForegroundHwnd;
        if (_clipboardHistoryWindow != null && _clipboardHistoryWindow.IsLoaded)
        {
            _clipboardHistoryWindow.UpdateTargetWindow(target);
            _clipboardHistoryWindow.Activate();
            return;
        }

        _clipboardHistoryWindow = new ClipboardHistoryWindow(target);
        _clipboardHistoryWindow.Closed += (_, _) => _clipboardHistoryWindow = null;
        _clipboardHistoryWindow.Show();
        _clipboardHistoryWindow.Activate();
    }

    public void ShowSettings()
    {
        ShowPreferences(initialTabIsHistory: false);
    }

    private void ExitApp()
    {
        _notifyIcon?.Dispose();
        _hotKeyManager?.Dispose();
        try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _hotKeyManager?.Dispose();
        base.OnExit(e);
    }
}
