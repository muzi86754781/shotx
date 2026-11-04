using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
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

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_CONTROL = 0x11;
    private const byte VK_C = 0x43;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 1. 单实例互斥检查
        _singleInstanceMutex = new Mutex(true, "ShotX_Windows_SingleInstance_Mutex", out bool isNewInstance);
        if (!isNewInstance)
        {
            MessageBox.Show("ShotX 已经在运行中，请按快捷键 Alt+A 呼出截图或检查右下角系统托盘。", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 2. 初始化偏好配置
        PreferencesStore.Load();

        // 3. 初始化系统托盘图标
        InitNotifyIcon();

        // 4. 初始化全局快捷键
        InitGlobalHotkeys();

        // 5. 启动后台剪贴板监听
        StartClipboardWatcher();
    }

    private void InitNotifyIcon()
    {
        var contextMenu = new ContextMenuStrip();

        // 1. 全能截图
        contextMenu.Items.Add("全能截图", null, (_, _) => ShowAllInOneBar());

        // 2. 区域截图
        contextMenu.Items.Add("区域截图 (Alt+A)", null, (_, _) => StartScreenshot(OverlayMode.Area));

        // 3. 上次区域截图
        var itemPrev = contextMenu.Items.Add("上次区域截图 (Alt+R)", null, (_, _) => CapturePreviousArea());
        itemPrev.Enabled = LastCaptureStore.HasPrevious;

        // 4. 全屏截图
        contextMenu.Items.Add("全屏截图 (Alt+Shift+A)", null, (_, _) => CaptureFullscreen());

        // 5. 窗口截图
        contextMenu.Items.Add("窗口截图 (Alt+W)", null, (_, _) => StartScreenshot(OverlayMode.Window));

        // 6. 定时截图
        var timerMenu = new ToolStripMenuItem("定时截图");
        timerMenu.DropDownItems.Add("3 秒", null, (_, _) => StartTimerCapture(3));
        timerMenu.DropDownItems.Add("5 秒", null, (_, _) => StartTimerCapture(5));
        timerMenu.DropDownItems.Add("10 秒", null, (_, _) => StartTimerCapture(10));
        contextMenu.Items.Add(timerMenu);

        // 7. 截图 OCR
        contextMenu.Items.Add("截图 OCR", null, (_, _) => StartScreenshot(OverlayMode.OCR));

        // 8. 从剪贴板贴图
        contextMenu.Items.Add("从剪贴板贴图 (Alt+F3)", null, (_, _) => OpenFromClipboard());

        // 9. 改图-打开图片
        contextMenu.Items.Add("改图-打开图片", null, (_, _) => OpenFileForEdit());

        // 10. 改图-从剪贴板打开
        contextMenu.Items.Add("改图-从剪贴板打开 (Alt+P)", null, (_, _) => OpenFromClipboard());

        // 11. 拾色器
        contextMenu.Items.Add("拾色器 (Alt+Shift+C)", null, (_, _) => StartScreenshot(OverlayMode.ColorPicker));

        contextMenu.Items.Add(new ToolStripSeparator());

        // 12. 屏幕录制
        contextMenu.Items.Add("屏幕录制 (Alt+Shift+R)", null, (_, _) => StartScreenRecording());

        // 13. 声音录制
        contextMenu.Items.Add("声音录制", null, (_, _) => StartAudioRecording());

        contextMenu.Items.Add(new ToolStripSeparator());

        // 14. 划词翻译
        contextMenu.Items.Add("划词翻译 (Alt+Shift+F)", null, (_, _) => TranslateSelection());

        // 15. 截图翻译
        contextMenu.Items.Add("截图翻译", null, (_, _) => StartScreenshot(OverlayMode.Translate));

        // 16. 输入翻译
        contextMenu.Items.Add("输入翻译 (Alt+V)", null, (_, _) => ShowTranslationWindow());

        contextMenu.Items.Add(new ToolStripSeparator());

        // 17. 剪贴板历史
        contextMenu.Items.Add("剪贴板历史 (Alt+C)", null, (_, _) => ShowClipboardHistoryWindow());

        contextMenu.Items.Add(new ToolStripSeparator());

        // 18. 隐藏桌面图标
        bool isDesktopVisible = DesktopIcons.IsVisible();
        contextMenu.Items.Add(isDesktopVisible ? "隐藏桌面图标" : "显示桌面图标", null, (_, _) => ToggleDesktopIcons());

        // 19. 历史记录
        contextMenu.Items.Add("历史记录", null, (_, _) => ShowPreferences(initialTabIsHistory: true));

        contextMenu.Items.Add(new ToolStripSeparator());

        // 20. 支持作者
        contextMenu.Items.Add("支持作者", null, (_, _) => ShowSupportAuthorWindow());

        // 21. 偏好设置
        contextMenu.Items.Add("偏好设置", null, (_, _) => ShowPreferences(initialTabIsHistory: false));

        // 22. 检查更新
        contextMenu.Items.Add("检查更新", null, (_, _) => CheckForUpdates());

        // 23. 关于 ShotX
        contextMenu.Items.Add("关于 ShotX", null, (_, _) => ShowPreferences(initialTabIsHistory: false));

        // 24. 退出 ShotX
        contextMenu.Items.Add("退出 ShotX", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "ShotX - 全能屏幕截图与录制神器",
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => StartScreenshot(OverlayMode.AllInOne);
    }

    private void InitGlobalHotkeys()
    {
        try
        {
            _hotKeyManager = new HotKeyManager();

            // Alt + A: 区域截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.A, () => Dispatcher.Invoke(() => StartScreenshot(OverlayMode.Area)));

            // Alt + R: 上次区域截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.R, () => Dispatcher.Invoke(CapturePreviousArea));

            // Alt + Shift + A: 全屏截图
            _hotKeyManager.Register(ModifierKeys.Alt | ModifierKeys.Shift, Key.A, () => Dispatcher.Invoke(CaptureFullscreen));

            // Alt + W: 窗口截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.W, () => Dispatcher.Invoke(() => StartScreenshot(OverlayMode.Window)));

            // Alt + S: 滚动长截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.S, () => Dispatcher.Invoke(() => StartScreenshot(OverlayMode.AllInOne)));

            // Alt + P: 改图 (从剪贴板打开)
            _hotKeyManager.Register(ModifierKeys.Alt, Key.P, () => Dispatcher.Invoke(OpenFromClipboard));

            // Alt + F3: 从剪贴板贴图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.F3, () => Dispatcher.Invoke(OpenFromClipboard));

            // Alt + Shift + C: 拾色器
            _hotKeyManager.Register(ModifierKeys.Alt | ModifierKeys.Shift, Key.C, () => Dispatcher.Invoke(() => StartScreenshot(OverlayMode.ColorPicker)));

            // Alt + Shift + R: 屏幕录制
            _hotKeyManager.Register(ModifierKeys.Alt | ModifierKeys.Shift, Key.R, () => Dispatcher.Invoke(StartScreenRecording));

            // Alt + Shift + F: 划词翻译
            _hotKeyManager.Register(ModifierKeys.Alt | ModifierKeys.Shift, Key.F, () => Dispatcher.Invoke(TranslateSelection));

            // Alt + V: 呼出翻译窗口
            _hotKeyManager.Register(ModifierKeys.Alt, Key.V, () => Dispatcher.Invoke(ShowTranslationWindow));

            // Alt + C: 呼出剪贴板历史窗口
            _hotKeyManager.Register(ModifierKeys.Alt, Key.C, () => Dispatcher.Invoke(ShowClipboardHistoryWindow));
        }
        catch
        {
        }
    }

    private void StartClipboardWatcher()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5)
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
                        ClipboardManagerStore.Add(txt);
                    }
                }
            }
            catch
            {
            }
        };
        timer.Start();
    }

    public void StartScreenshot(OverlayMode mode = OverlayMode.AllInOne)
    {
        if (_currentOverlay != null && _currentOverlay.IsVisible)
        {
            _currentOverlay.Activate();
            return;
        }

        _currentOverlay = new OverlayWindow(mode);
        _currentOverlay.Closed += (_, _) => _currentOverlay = null;
        _currentOverlay.Show();
    }

    public void StartTimerCapture(int seconds)
    {
        var win = new CountdownWindow(seconds, () => Dispatcher.Invoke(() => StartScreenshot(OverlayMode.AllInOne)));
        win.Show();
    }

    public void ShowAllInOneBar()
    {
        var bar = new AllInOneBarWindow();
        bar.Show();
    }

    public void ShowPreferences(bool initialTabIsHistory = false)
    {
        var win = new PreferencesWindow(initialTabIsHistory);
        win.Show();
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

    public void TranslateSelection()
    {
        try
        {
            keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, 0, UIntPtr.Zero);
            keybd_event(VK_C, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);

            Task.Delay(150).ContinueWith(_ =>
            {
                Dispatcher.Invoke(() =>
                {
                    string text = System.Windows.Clipboard.GetText().Trim();
                    var win = new TranslationWindow();
                    if (!string.IsNullOrEmpty(text)) win.SetSourceText(text);
                    win.Show();
                });
            });
        }
        catch
        {
            ShowTranslationWindow();
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
            MessageBox.Show("暂无上一次截图区域记录，请先使用 Alt+A 框选一次截图。", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
            StartScreenshot();
            return;
        }

        if (_currentOverlay != null && _currentOverlay.IsVisible)
        {
            _currentOverlay.Activate();
            return;
        }

        _currentOverlay = new OverlayWindow(OverlayMode.Area, LastCaptureStore.LastRect);
        _currentOverlay.Closed += (_, _) => _currentOverlay = null;
        _currentOverlay.Show();
    }

    public void CaptureFullscreen()
    {
        if (_currentOverlay != null && _currentOverlay.IsVisible)
        {
            _currentOverlay.Activate();
            return;
        }

        var fullRect = new Rect(0, 0, SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        _currentOverlay = new OverlayWindow(OverlayMode.Area, fullRect);
        _currentOverlay.Closed += (_, _) => _currentOverlay = null;
        _currentOverlay.Show();
    }

    public void OpenFileForEdit()
    {
        try
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "图片文件 (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|所有文件 (*.*)|*.*",
                Title = "ShotX - 选择要标注的图片"
            };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                var bmp = new Bitmap(ofd.FileName);
                var pinned = new PinnedWindow(bmp, new System.Windows.Point(200, 200));
                pinned.Show();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show($"打开图片失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void ToggleDesktopIcons()
    {
        DesktopIcons.Toggle();
    }

    public void OpenFromClipboard()
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
            MessageBox.Show("剪贴板中暂无有效图片，请先复制一张图片后再按 Alt+P 改图。", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"读取剪贴板图片失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static Bitmap BitmapSourceToBitmap(BitmapSource source)
    {
        var bmp = new Bitmap(source.PixelWidth, source.PixelHeight, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        source.CopyPixels(Int32Rect.Empty, data.Scan0, data.Height * data.Stride, data.Stride);
        bmp.UnlockBits(data);
        return bmp;
    }

    public void ShowTranslationWindow()
    {
        var win = new TranslationWindow();
        win.Show();
    }

    public void ShowClipboardHistoryWindow()
    {
        var win = new ClipboardHistoryWindow();
        win.Show();
    }

    public void ShowSettings()
    {
        var settingsWin = new SettingsWindow();
        settingsWin.Show();
    }

    private void ExitApp()
    {
        _notifyIcon?.Dispose();
        _hotKeyManager?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _notifyIcon?.Dispose();
        _hotKeyManager?.Dispose();
        base.OnExit(e);
    }
}
