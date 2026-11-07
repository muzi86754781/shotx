using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using MessageBox = System.Windows.MessageBox;

namespace ShotX.Windows.Views;

public partial class AboutWindow : Window
{
    private static readonly HttpClient HttpClient = new();

    public AboutWindow()
    {
        InitializeComponent();
    }

    private void BtnGitHub_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://github.com/muzi86754781/shotx") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            HttpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ShotX-Windows-App");
            string json = await HttpClient.GetStringAsync("https://api.github.com/repos/muzi86754781/shotx/releases/latest");
            using var doc = JsonDocument.Parse(json);
            string tagName = doc.RootElement.GetProperty("tag_name").GetString() ?? "";

            if (!string.IsNullOrEmpty(tagName) && tagName != "v1.9.25")
            {
                if (MessageBox.Show($"发现新版本 {tagName}，是否前往 GitHub 下载最新版？", "检查更新", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                {
                    Process.Start(new ProcessStartInfo("https://github.com/muzi86754781/shotx/releases/latest") { UseShellExecute = true });
                }
            }
            else
            {
                MessageBox.Show("当前已是最新版本 (v1.9.25)。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch
        {
            MessageBox.Show("当前已是最新版本 (v1.9.25)。", "检查更新", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

