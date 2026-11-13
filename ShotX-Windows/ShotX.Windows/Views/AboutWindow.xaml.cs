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

            if (!string.IsNullOrEmpty(tagName) && tagName != "v1.9.31")
            {
                ModernToast.ShowInfo($"发现新版本 {tagName}，正在前往发布页面...");
                Process.Start(new ProcessStartInfo("https://github.com/muzi86754781/shotx/releases/latest") { UseShellExecute = true });
            }
            else
            {
                ModernToast.ShowSuccess("当前已是最新版本 (v1.9.31)。");
            }
        }
        catch
        {
            ModernToast.ShowSuccess("当前已是最新版本 (v1.9.31)。");
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}

