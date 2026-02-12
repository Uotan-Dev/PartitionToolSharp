using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _useDarkTheme = true;

    partial void OnUseDarkThemeChanged(bool value)
    {
        var theme = value ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = theme;
        }
    }
}
