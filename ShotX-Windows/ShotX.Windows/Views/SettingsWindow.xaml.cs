using System.Windows;
using System.Windows.Forms;
using ShotX.Windows.Core;

namespace ShotX.Windows.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        LoadSettings();
    }

    private void LoadSettings()
    {
        var cfg = PreferencesStore.Current;
        ChkAutoStart.IsChecked = cfg.LaunchAtStartup;
        ChkPlaySound.IsChecked = cfg.PlaySound;
        TxtSavePath.Text = cfg.SavePath;
    }

    private void ChkAutoStart_Click(object sender, RoutedEventArgs e)
    {
        PreferencesStore.SetLaunchAtStartup(ChkAutoStart.IsChecked == true);
    }

    private void ChkPlaySound_Click(object sender, RoutedEventArgs e)
    {
        PreferencesStore.Current.PlaySound = ChkPlaySound.IsChecked == true;
        PreferencesStore.Save();
    }

    private void BtnBrowseSavePath_Click(object sender, RoutedEventArgs e)
    {
        using var fbd = new FolderBrowserDialog
        {
            SelectedPath = PreferencesStore.Current.SavePath
        };
        if (fbd.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            PreferencesStore.Current.SavePath = fbd.SelectedPath;
            PreferencesStore.Save();
            TxtSavePath.Text = fbd.SelectedPath;
        }
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
