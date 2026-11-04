using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ShotX.Windows.Core;
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
        contextMenu.Items.Add("区域截图 (Alt+A)", null, (_, _) => StartScreenshot());
        contextMenu.Items.Add("上次区域截图 (Alt+R)", null, (_, _) => CapturePreviousArea());
        contextMenu.Items.Add("全屏截图 (Alt+Shift+A)", null, (_, _) => CaptureFullscreen());
        contextMenu.Items.Add("滚动长截图 (Alt+S)", null, (_, _) => StartScreenshot());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("改图 - 从剪贴板打开 (Alt+P)", null, (_, _) => OpenFromClipboard());
        contextMenu.Items.Add("改图 - 打开本地图片", null, (_, _) => OpenFileForEdit());
        contextMenu.Items.Add("从剪贴板贴图 (Alt+F3)", null, (_, _) => OpenFromClipboard());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("隐藏 / 显示桌面图标", null, (_, _) => ToggleDesktopIcons());
        contextMenu.Items.Add("翻译窗口 (Alt+V)", null, (_, _) => ShowTranslationWindow());
        contextMenu.Items.Add("剪贴板历史 (Alt+C)", null, (_, _) => ShowClipboardHistoryWindow());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("偏好设置...", null, (_, _) => ShowSettings());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("退出 ShotX", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "ShotX - 屏幕截图与全能标注工具",
            Icon = SystemIcons.Application,
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => StartScreenshot();
    }

    private void InitGlobalHotkeys()
    {
        try
        {
            _hotKeyManager = new HotKeyManager();

            // Alt + A: 区域截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.A, () => Dispatcher.Invoke(StartScreenshot));

            // Alt + R: 上次区域截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.R, () => Dispatcher.Invoke(CapturePreviousArea));

            // Alt + Shift + A: 全屏截图
            _hotKeyManager.Register(ModifierKeys.Alt | ModifierKeys.Shift, Key.A, () => Dispatcher.Invoke(CaptureFullscreen));

            // Alt + S: 滚动长截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.S, () => Dispatcher.Invoke(StartScreenshot));

            // Alt + P: 改图 (从剪贴板打开)
            _hotKeyManager.Register(ModifierKeys.Alt, Key.P, () => Dispatcher.Invoke(OpenFromClipboard));

            // Alt + F3: 从剪贴板贴图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.F3, () => Dispatcher.Invoke(OpenFromClipboard));

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

    public void StartScreenshot()
    {
        if (_currentOverlay != null && _currentOverlay.IsVisible)
        {
            _currentOverlay.Activate();
            return;
        }

        _currentOverlay = new OverlayWindow();
        _currentOverlay.Closed += (_, _) => _currentOverlay = null;
        _currentOverlay.Show();
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

        _currentOverlay = new OverlayWindow(LastCaptureStore.LastRect);
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
        _currentOverlay = new OverlayWindow(fullRect);
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
