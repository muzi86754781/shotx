using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ShotX.Windows.Core;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public partial class ClipboardHistoryWindow : Window
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    private const byte VK_SHIFT = 0x10;
    private const byte VK_CONTROL = 0x11;
    private const byte VK_MENU = 0x12; // Alt
    private const byte VK_V = 0x56;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    public class ClipboardItemViewModel
    {
        public string Title { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
        public string FullText { get; set; } = string.Empty;
        public string TimeString { get; set; } = string.Empty;
        public HistoryItem? RawItem { get; set; }
    }

    private readonly List<ClipboardItemViewModel> _allItems = new();
    private readonly DispatcherTimer _toastTimer;
    private IntPtr _targetExternalHwnd = IntPtr.Zero;

    public ClipboardHistoryWindow(IntPtr targetExternalHwnd = default)
    {
        _targetExternalHwnd = targetExternalHwnd != IntPtr.Zero ? targetExternalHwnd : App.LastExternalForegroundHwnd;

        _toastTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1800)
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            if (ToastBanner != null)
            {
                ToastBanner.Visibility = Visibility.Collapsed;
            }
        };

        InitializeComponent();
        LoadData();
    }

    public void UpdateTargetWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
        {
            _targetExternalHwnd = hwnd;
        }
        LoadData();
    }

    private void ShowToast(string message)
    {
        if (TxtToastMessage == null || ToastBanner == null) return;
        TxtToastMessage.Text = message;
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
        }
    }

    private void LoadData()
    {
        _allItems.Clear();
        var rawList = HistoryStore.GetItemsByType(HistoryType.Clipboard);

        if (rawList.Count == 0)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        HistoryStore.Add(HistoryType.Clipboard, text, text);
                        rawList = HistoryStore.GetItemsByType(HistoryType.Clipboard);
                    }
                }
            }
            catch { }
        }

        foreach (var it in rawList)
        {
            string line1 = it.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? it.Content;
            string preview = it.Content.Replace("\r\n", " ").Replace("\n", " ").Trim();
            if (preview.Length > 85) preview = preview[..85] + "...";

            _allItems.Add(new ClipboardItemViewModel
            {
                Title = line1,
                Snippet = preview,
                FullText = it.Content,
                TimeString = it.Timestamp.ToString("MM-dd HH:mm:ss"),
                RawItem = it
            });
        }

        RefreshFilteredList();
    }

    private void RefreshFilteredList()
    {
        if (TxtSearch == null || LstHistory == null || TxtFooter == null) return;

        string query = TxtSearch.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems.ToList()
            : _allItems.Where(i => i.FullText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        // 强制重新绑定新列表引用，确保删除或清空后 WPF ListBox 立即刷新 (解决需求 8)
        LstHistory.ItemsSource = null;
        LstHistory.ItemsSource = filtered;

        if (EmptyStatePanel != null)
        {
            EmptyStatePanel.Visibility = filtered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        TxtFooter.Text = $"双击任意记录直接粘贴到光标位置 · 共 {filtered.Count} 条记录";
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (TxtPlaceholder != null && TxtSearch != null)
        {
            TxtPlaceholder.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        RefreshFilteredList();
    }

    private static T? FindVisualParent<T>(DependencyObject? child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T matched) return matched;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }

    private async void LstHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 如果双击在右侧复制/删除按钮上，不触发双击粘贴
        if (e.OriginalSource is DependencyObject dep && FindVisualParent<Button>(dep) != null)
        {
            return;
        }

        if (LstHistory.SelectedItem is ClipboardItemViewModel model)
        {
            try
            {
                // 1. 设置剪贴板内容（带重试机制，防止被系统安全软件临时占用）
                for (int retry = 0; retry < 5; retry++)
                {
                    try
                    {
                        Clipboard.SetDataObject(model.FullText, true);
                        break;
                    }
                    catch
                    {
                        await Task.Delay(25);
                    }
                }

                IntPtr targetHwnd = _targetExternalHwnd != IntPtr.Zero
                    ? _targetExternalHwnd
                    : App.LastExternalForegroundHwnd;

                // 2. 隐藏剪贴板窗口，使外部目标窗口（如记事本、VS Code、浏览器）恢复原本输入焦点 (解决需求 4)
                Hide();

                // 3. 强力激活目标窗口与输入线程
                if (targetHwnd != IntPtr.Zero)
                {
                    uint currentThreadId = GetCurrentThreadId();
                    uint targetThreadId = GetWindowThreadProcessId(targetHwnd, out _);

                    AttachThreadInput(currentThreadId, targetThreadId, true);
                    SetForegroundWindow(targetHwnd);
                    SetFocus(targetHwnd);
                    AttachThreadInput(currentThreadId, targetThreadId, false);

                    await Task.Delay(100);
                }

                // 4. 彻底释放修饰键 (Alt / Shift / Ctrl) 防止快捷键状态冲突
                keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_SHIFT, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                await Task.Delay(25);

                // 5. 模拟 Ctrl+V 快速粘贴到光标所在窗口
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch
            {
                try { Clipboard.SetText(model.FullText); } catch { }
            }
        }
    }

    private void BtnItemCopy_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel model)
        {
            try
            {
                Clipboard.SetText(model.FullText);
                ShowToast("✓ 已复制到剪贴板");
            }
            catch { }
        }
    }

    private void BtnItemDelete_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel model)
        {
            if (model.RawItem != null)
            {
                HistoryStore.Delete(model.RawItem);
            }
            _allItems.Remove(model);
            RefreshFilteredList();
            ShowToast("🗑️ 已删除该条记录");
        }
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        HistoryStore.Clear(HistoryType.Clipboard);
        _allItems.Clear();
        RefreshFilteredList();
        ShowToast("🗑️ 已清空全部剪贴板记录");
    }
}
