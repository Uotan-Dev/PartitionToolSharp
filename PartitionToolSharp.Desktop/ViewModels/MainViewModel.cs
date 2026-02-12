using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PartitionToolSharp.Desktop.Models;
using PartitionToolSharp.Desktop.Services;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject, IRecipient<OpenImageRequestMessage>
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
        WeakReferenceMessenger.Default.Register(this);

        if (!string.IsNullOrEmpty(ConfigService.Current.LastOpenedFilePath) && File.Exists(ConfigService.Current.LastOpenedFilePath))
        {
            CurrentView = _partitionManagerVM;
        }
    }

    public async void Receive(OpenImageRequestMessage message) => await OpenImageGlobalAsync();

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

    [RelayCommand]
    private async Task OpenImageGlobalAsync()
    {
        Navigate("PartitionManager");
        await _partitionManagerVM.OpenFileAsync();
    }
}
