using Avalonia;
using Avalonia.Styling;
using CommunityToolkit.Mvvm.ComponentModel;
using PartitionToolSharp.Desktop.Services;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _useDarkTheme;

    public SettingsViewModel()
    {
        _useDarkTheme = ConfigService.Current.Theme == "System"
            ? Application.Current?.ActualThemeVariant == ThemeVariant.Dark
            : ConfigService.Current.Theme == "Dark";
    }

    partial void OnUseDarkThemeChanged(bool value)
    {
        ConfigService.Current.Theme = value ? "Dark" : "Light";
        ConfigService.Save();

        var theme = value ? ThemeVariant.Dark : ThemeVariant.Light;
        if (Application.Current != null)
        {
            Application.Current.RequestedThemeVariant = theme;
        }
    }
}
