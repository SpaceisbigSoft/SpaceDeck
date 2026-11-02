using System.Windows;
using System.Windows.Controls;
using SpaceDeck.App.ViewModels;

namespace SpaceDeck.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
    }

    /// <summary>Shows the saved ntfy token (masked); also when the section gets a new view model.</summary>
    private void NtfyTokenBox_Loaded(object sender, RoutedEventArgs e)
    {
        if (NtfyTokenBox.DataContext is RemoteSettingsViewModel vm)
        {
            NtfyTokenBox.Password = vm.NtfyAccessToken ?? "";
        }
    }

    /// <summary>Saves the token when the box loses focus (like the other text boxes), not on every key.</summary>
    private void NtfyTokenBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (NtfyTokenBox.DataContext is RemoteSettingsViewModel vm
            && !string.Equals(vm.NtfyAccessToken ?? "", NtfyTokenBox.Password.Trim(), StringComparison.Ordinal))
        {
            vm.NtfyAccessToken = NtfyTokenBox.Password;
        }
    }
}
