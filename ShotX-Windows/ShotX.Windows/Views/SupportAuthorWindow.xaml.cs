using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace ShotX.Windows.Views;

public partial class SupportAuthorWindow : Window
{
    public SupportAuthorWindow()
    {
        InitializeComponent();

        try
        {
            // Try loading Resources/shou.jpg
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "Resources", "shou.jpg");
            if (File.Exists(path))
            {
                var bi = new BitmapImage();
                bi.BeginInit();
                bi.UriSource = new Uri(path);
                bi.CacheOption = BitmapCacheOption.OnLoad;
                bi.EndInit();
                ImgQrCode.Source = bi;
            }
        }
        catch
        {
        }
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

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
