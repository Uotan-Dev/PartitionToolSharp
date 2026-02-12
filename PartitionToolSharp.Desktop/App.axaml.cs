using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using PartitionToolSharp.Desktop.Services;
using PartitionToolSharp.Desktop.ViewModels;
using PartitionToolSharp.Desktop.Views;

namespace PartitionToolSharp.Desktop;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        ConfigService.Load();

        // Apply theme
        RequestedThemeVariant = ConfigService.Current.Theme switch
        {
            "Dark" => ThemeVariant.Dark,
            "Light" => ThemeVariant.Light,
            _ => ThemeVariant.Default // Follow System
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainView = new MainWindow
            {
                DataContext = new MainViewModel(),
                Width = ConfigService.Current.WindowWidth,
                Height = ConfigService.Current.WindowHeight,
            };

            desktop.MainWindow = mainView;

            desktop.ShutdownRequested += (s, e) =>
            {
                if (desktop.MainWindow != null)
                {
                    ConfigService.Current.WindowWidth = desktop.MainWindow.Width;
                    ConfigService.Current.WindowHeight = desktop.MainWindow.Height;
                }
                ConfigService.Save();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
