using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace ShotX.Windows.Views;

public class ClipboardItem
{
    public string Content { get; set; } = string.Empty;
    public string PreviewText { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public string TimeString => Timestamp.ToString("HH:mm:ss");
}

public static class ClipboardManagerStore
{
    public static List<ClipboardItem> History { get; } = new();

    public static void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        text = text.Trim();
        History.RemoveAll(x => x.Content == text);
        History.Insert(0, new ClipboardItem
        {
            Content = text,
            PreviewText = text.Replace("\r", " ").Replace("\n", " "),
            Timestamp = DateTime.Now
        });
        if (History.Count > 100)
        {
            History.RemoveAt(History.Count - 1);
        }
    }
}

public partial class ClipboardHistoryWindow : Window
{
    private readonly ObservableCollection<ClipboardItem> _displayItems = new();

    public ClipboardHistoryWindow()
    {
        InitializeComponent();
        RefreshList();
        TxtSearch.Focus();
    }

    private void RefreshList()
    {
        _displayItems.Clear();
        string filter = TxtSearch.Text.Trim();
        foreach (var item in ClipboardManagerStore.History)
        {
            if (string.IsNullOrEmpty(filter) || item.Content.Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                _displayItems.Add(item);
            }
        }
        LstHistory.ItemsSource = _displayItems;
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList();
    }

    private void LstHistory_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LstHistory.SelectedItem is ClipboardItem item)
        {
            System.Windows.Clipboard.SetText(item.Content);
            Close();
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        ClipboardManagerStore.History.Clear();
        RefreshList();
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

    private void Window_Deactivated(object sender, EventArgs e)
    {
        Close();
    }
}

