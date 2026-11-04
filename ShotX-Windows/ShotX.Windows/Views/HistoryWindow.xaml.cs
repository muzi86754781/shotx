using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ShotX.Windows.Core;

namespace ShotX.Windows.Views;

public partial class HistoryWindow : Window
{
    public HistoryWindow()
    {
        InitializeComponent();
        RefreshList();
    }

    private void RefreshList()
    {
        var all = HistoryStore.Entries;
        string filter = TxtSearch.Text.Trim().ToLowerInvariant();

        var filtered = string.IsNullOrEmpty(filter)
            ? all
            : all.Where(e => e.FileName.ToLowerInvariant().Contains(filter) || e.Kind.ToLowerInvariant().Contains(filter) || e.TextContent.ToLowerInvariant().Contains(filter)).ToList();

        LstEntries.ItemsSource = filtered;
        TxtTotalCount.Text = $"({filtered.Count} 项)";

        if (filtered.Count > 0 && LstEntries.SelectedItem == null)
        {
            LstEntries.SelectedIndex = 0;
        }
        else if (filtered.Count == 0)
        {
            ImgPreview.Source = null;
            TxtPreviewOcr.Visibility = Visibility.Collapsed;
            TxtEmptyPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshList();
    }

    private void LstEntries_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LstEntries.SelectedItem is HistoryEntry item)
        {
            TxtEmptyPlaceholder.Visibility = Visibility.Collapsed;
            if (item.Kind == "OCR")
            {
                ImgPreview.Source = null;
                ImgPreview.Visibility = Visibility.Collapsed;
                TxtPreviewOcr.Visibility = Visibility.Visible;
                TxtPreviewOcr.Text = item.TextContent;
            }
            else
            {
                TxtPreviewOcr.Visibility = Visibility.Collapsed;
                ImgPreview.Visibility = Visibility.Visible;
                if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
                {
                    try
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.UriSource = new Uri(item.FilePath);
                        bi.EndInit();
                        ImgPreview.Source = bi;
                    }
                    catch
                    {
                        ImgPreview.Source = null;
                    }
                }
                else
                {
                    ImgPreview.Source = null;
                }
            }
        }
    }

    private void LstEntries_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        BtnCopy_Click(sender, e);
    }

    private void BtnCopy_Click(object sender, RoutedEventArgs e)
    {
        if (LstEntries.SelectedItem is HistoryEntry item)
        {
            if (item.Kind == "OCR")
            {
                Clipboard.SetText(item.TextContent);
                MessageBox.Show("OCR 文本已复制到剪贴板！", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                try
                {
                    var bi = new BitmapImage(new Uri(item.FilePath));
                    Clipboard.SetImage(bi);
                    MessageBox.Show("图片已成功复制到剪贴板！", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"复制图片失败: {ex.Message}", "ShotX", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        if (LstEntries.SelectedItem is HistoryEntry item && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
        {
            using var sfd = new System.Windows.Forms.SaveFileDialog
            {
                Filter = "PNG Image (*.png)|*.png|All Files (*.*)|*.*",
                FileName = item.FileName
            };
            if (sfd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                File.Copy(item.FilePath, sfd.FileName, true);
                MessageBox.Show("图片已成功另存为！", "ShotX", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
    }

    private void BtnOpenFile_Click(object sender, RoutedEventArgs e)
    {
        if (LstEntries.SelectedItem is HistoryEntry item && !string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
        {
            Process.Start("explorer.exe", $"/select,\"{item.FilePath}\"");
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (LstEntries.SelectedItem is HistoryEntry item)
        {
            HistoryStore.Delete(item);
            RefreshList();
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show("确定要清空全部截图与 OCR 历史记录吗？", "ShotX 历史记录", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            HistoryStore.Clear();
            RefreshList();
        }
    }
}
