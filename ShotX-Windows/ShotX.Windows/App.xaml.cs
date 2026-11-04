using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
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
            MessageBox.Show("ShotX 已经在运行中，请按 Alt+A 呼出截图或检查托盘图标。", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        // 2. 初始化偏好配置
        PreferencesStore.Load();

        // 3. 初始化系统托盘图标
        InitNotifyIcon();

        // 4. 初始化全局快捷键
        InitGlobalHotkeys();
    }

    private void InitNotifyIcon()
    {
        var contextMenu = new ContextMenuStrip();
        contextMenu.Items.Add("区域截图 (Alt+A)", null, (_, _) => StartScreenshot());
        contextMenu.Items.Add("设置", null, (_, _) => ShowSettings());
        contextMenu.Items.Add(new ToolStripSeparator());
        contextMenu.Items.Add("退出 ShotX", null, (_, _) => ExitApp());

        _notifyIcon = new NotifyIcon
        {
            Text = "ShotX - 截图与标注工具 (Alt+A)",
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
            // Alt + A: 截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.A, () =>
            {
                Dispatcher.Invoke(StartScreenshot);
            });
            // Alt + S: 长截图
            _hotKeyManager.Register(ModifierKeys.Alt, Key.S, () =>
            {
                Dispatcher.Invoke(StartScreenshot);
            });
        }
        catch
        {
        }
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
