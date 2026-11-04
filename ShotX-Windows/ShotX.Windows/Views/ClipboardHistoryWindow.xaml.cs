using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ShotX.Windows.Core;
using Application = System.Windows.Application;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public partial class ClipboardHistoryWindow : Window
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const byte VK_CONTROL = 0x11;
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

    public ClipboardHistoryWindow()
    {
        InitializeComponent();
        LoadData();
    }

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
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
            // 如果历史为空，提供默认或当前剪贴板内容
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

        foreach (var it in rawList)
        {
            string line1 = it.Content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? it.Content;
            string preview = it.Content.Length > 80 ? it.Content.Substring(0, 80) + "..." : it.Content;
            _allItems.Add(new ClipboardItemViewModel
            {
                Title = line1,
                Snippet = preview,
                FullText = it.Content,
                TimeString = it.Timestamp.ToString("HH:mm"),
                RawItem = it
            });
        }

        RefreshFilteredList();
    }

    private void RefreshFilteredList()
    {
        string query = TxtSearch.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _allItems
            : _allItems.Where(i => i.FullText.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0).ToList();

        LstHistory.ItemsSource = filtered;
        TxtFooter.Text = $"双击条目直接在光标处粘贴 (窗口不关闭可连续操作) · {filtered.Count} 条记录";
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        TxtPlaceholder.Visibility = string.IsNullOrEmpty(TxtSearch.Text) ? Visibility.Visible : Visibility.Collapsed;
        RefreshFilteredList();
    }

    private void LstHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstHistory.SelectedItem is ClipboardItemViewModel model)
        {
            try
            {
                Clipboard.SetText(model.FullText);

                // 模拟 Ctrl+V 粘贴
                keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, 0, UIntPtr.Zero);
                keybd_event(VK_V, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            }
            catch { }
        }
    }

    private void BtnItemCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel model)
        {
            try
            {
                Clipboard.SetText(model.FullText);
            }
            catch { }
        }
    }

    private void BtnItemDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is ClipboardItemViewModel model)
        {
            if (model.RawItem != null)
            {
                HistoryStore.Delete(model.RawItem);
            }
            _allItems.Remove(model);
            RefreshFilteredList();
        }
    }

    private void BtnClearAll_Click(object sender, RoutedEventArgs e)
    {
        HistoryStore.Clear(HistoryType.Clipboard);
        _allItems.Clear();
        RefreshFilteredList();
    }
}
