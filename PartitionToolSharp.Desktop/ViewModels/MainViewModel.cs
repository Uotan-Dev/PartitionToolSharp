using System.IO;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using PartitionToolSharp.Desktop.Models;
using PartitionToolSharp.Desktop.Services;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class MainViewModel : ObservableObject,
    IRecipient<OpenImageRequestMessage>,
    IRecipient<FlashPartitionMessage>
{
    [ObservableProperty]
    private object? _currentView;

    [ObservableProperty]
    private string _activeMenuHeader = "Dashboard";

    private readonly DashboardViewModel _dashboardVM = new();
    private readonly PartitionManagerViewModel _partitionManagerVM = new();
    private readonly FlasherViewModel _flasherVM = new();
    private readonly SettingsViewModel _settingsVM = new();

    public MainViewModel()
    {
        CurrentView = _dashboardVM;
        ActiveMenuHeader = "Dashboard";
        WeakReferenceMessenger.Default.Register<OpenImageRequestMessage>(this);
        WeakReferenceMessenger.Default.Register<FlashPartitionMessage>(this);

        if (!string.IsNullOrEmpty(ConfigService.Current.LastOpenedFilePath) && File.Exists(ConfigService.Current.LastOpenedFilePath))
        {
            CurrentView = _partitionManagerVM;
            ActiveMenuHeader = "Partition Manager";
        }
    }

    public async void Receive(OpenImageRequestMessage message) => await OpenImageGlobalAsync(message.FilePath);

    public void Receive(FlashPartitionMessage message)
    {
        _flasherVM.PartitionName = message.PartitionName;
        _flasherVM.SelectedImagePath = message.ImagePath ?? "";

        // Handle direct data stream
        if (message.DataStream != null)
        {
            _flasherVM.SetDirectStream(message.DataStream, message.DataLength);
        }

        _flasherVM.Logs.Clear();
        if (message.PartitionName.Equals("super", System.StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(message.ImagePath))
        {
            _flasherVM.Log($"[联动] 准备刷入当前打开的完整镜像到 super 分区");
            _flasherVM.Log($"[路径] {message.ImagePath}");
        }
        else if (message.DataStream != null)
        {
            _flasherVM.Log($"[联动] 准备直接从镜像流刷入分区: {message.PartitionName}");
            _flasherVM.Log($"[大小] {message.DataLength / 1024.0 / 1024.0:F2} MiB");
        }
        else
        {
            _flasherVM.Log($"[联动] 准备刷入到分区: {message.PartitionName}");
            _flasherVM.Log($"请选择要刷入的镜像文件...");
        }

        Navigate("Flasher");
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

        ActiveMenuHeader = target switch
        {
            "Dashboard" => "Dashboard",
            "PartitionManager" => "Partition Manager",
            "Flasher" => "Flasher & Images",
            "Settings" => "Settings",
            _ => ActiveMenuHeader
        };
    }

    [RelayCommand]
    private async Task OpenImageGlobalAsync(string? path = null)
    {
        Navigate("PartitionManager");
        if (string.IsNullOrEmpty(path))
        {
            await _partitionManagerVM.OpenFileAsync();
        }
        else
        {
            await _partitionManagerVM.LoadFileAsync(path);
        }
    }
}
