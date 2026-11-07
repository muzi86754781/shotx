using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using ShotX.Windows.Core;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

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
                ModernToast.ShowSuccess("OCR 文本已复制到剪贴板");
            }
            else if (!string.IsNullOrEmpty(item.FilePath) && File.Exists(item.FilePath))
            {
                try
                {
                    var bi = new BitmapImage(new Uri(item.FilePath));
                    Clipboard.SetImage(bi);
                    ModernToast.ShowSuccess("图片已成功复制到剪贴板");
                }
                catch (Exception ex)
                {
                    ModernToast.ShowWarning($"复制图片失败: {ex.Message}");
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
                ModernToast.ShowSuccess("图片已成功另存为");
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
            ModernToast.ShowInfo("已删除该条记录");
        }
    }

    private void BtnClear_Click(object sender, RoutedEventArgs e)
    {
        HistoryStore.Clear();
        RefreshList();
        ModernToast.ShowInfo("已清空历史记录");
    }
}

