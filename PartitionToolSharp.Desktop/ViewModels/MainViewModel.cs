using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private object? _currentView;

    private readonly DashboardViewModel _dashboardVM = new();
    private readonly PartitionManagerViewModel _partitionManagerVM = new();
    private readonly FlasherViewModel _flasherVM = new();
    private readonly SettingsViewModel _settingsVM = new();

    public MainViewModel()
    {
        CurrentView = _dashboardVM;
    }

    [RelayCommand]
    private void Navigate(string target)
    {
        CurrentView = target switch
        {
            "Dashboard" => _dashboardVM,
            "PartitionManager" => _partitionManagerVM,
            "Flasher" => _flasherVM,
            "Settings" => _settingsVM,
            _ => CurrentView
        };
    }
}

public class DashboardViewModel : ObservableObject { }
public class FlasherViewModel : ObservableObject { }
public class SettingsViewModel : ObservableObject { }
