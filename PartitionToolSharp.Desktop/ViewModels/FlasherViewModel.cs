using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibSparseSharp;
using Potato.Fastboot;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class FlasherViewModel : ObservableObject
{
    [ObservableProperty]
    private string _status = "Ready";

    [ObservableProperty]
    private string? _selectedDevice;

    [ObservableProperty]
    private string _partitionName = "";

    [ObservableProperty]
    private string _selectedImagePath = "";

    [ObservableProperty]
    private bool _isBusy;

    public ObservableCollection<string> ConnectedDevices { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    private void Log(string message) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}"));

    [RelayCommand]
    private void RefreshDevices()
    {
        try
        {
            var devices = Fastboot.GetDevices();
            ConnectedDevices.Clear();
            foreach (var device in devices)
            {
                ConnectedDevices.Add(device);
            }

            if (ConnectedDevices.Count > 0 && SelectedDevice == null)
            {
                SelectedDevice = ConnectedDevices[0];
            }
            
            Log($"Found {ConnectedDevices.Count} device(s).");
        }
        catch (Exception ex)
        {
            Log($"Error refreshing devices: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task SelectImageAsync()
    {
        var storage = GetStorage();
        if (storage == null) return;

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Image to Flash",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Image Files") { Patterns = ["*.img", "*.bin"] }]
        });

        if (result.Count > 0)
        {
            SelectedImagePath = result[0].Path.LocalPath;
            if (string.IsNullOrEmpty(PartitionName))
            {
                PartitionName = Path.GetFileNameWithoutExtension(SelectedImagePath);
            }
        }
    }

    [RelayCommand]
    private async Task FlashAsync()
    {
        if (string.IsNullOrEmpty(SelectedDevice))
        {
            await Ursa.Controls.MessageBox.ShowOverlayAsync("未识别到设备，请先刷新并选择设备。", "提示");
            return;
        }

        if (string.IsNullOrEmpty(PartitionName))
        {
            await Ursa.Controls.MessageBox.ShowOverlayAsync("请输入目标分区名称。", "提示");
            return;
        }

        if (string.IsNullOrEmpty(SelectedImagePath) || !File.Exists(SelectedImagePath))
        {
            await Ursa.Controls.MessageBox.ShowOverlayAsync("请选择有效的镜像文件。", "提示");
            return;
        }

        IsBusy = true;
        Status = "Flashing...";
        Log($"Starting flash: {SelectedImagePath} -> {PartitionName} on {SelectedDevice}");

        try
        {
            await Task.Run(() =>
            {
                var fb = new Fastboot(SelectedDevice);
                try
                {
                    Log("Connecting to device...");
                    fb.Connect();
                    
                    Log("Uploading data...");
                    fb.UploadData(SelectedImagePath);
                    
                    Log($"Executing flash:{PartitionName} command...");
                    var resp = fb.Command($"flash:{PartitionName}");
                    
                    if (resp.Status == Fastboot.Status.Okay)
                    {
                        Log("Flash successful!");
                    }
                    else
                    {
                        Log($"Flash failed: {resp.Payload}");
                        throw new Exception(resp.Payload);
                    }
                }
                finally
                {
                    fb.Disconnect();
                }
            });

            await Ursa.Controls.MessageBox.ShowOverlayAsync("刷入成功！", "提示");
        }
        catch (Exception ex)
        {
            Log($"Flash Error: {ex.Message}");
            await Ursa.Controls.MessageBox.ShowOverlayAsync($"刷入失败: {ex.Message}", "错误");
        }
        finally
        {
            IsBusy = false;
            Status = "Ready";
        }
    }

    [RelayCommand]
    private async Task RebootAsync()
    {
        if (string.IsNullOrEmpty(SelectedDevice))
        {
            await Ursa.Controls.MessageBox.ShowOverlayAsync("未识别到设备。", "提示");
            return;
        }

        try
        {
            await Task.Run(() =>
            {
                var fb = new Fastboot(SelectedDevice);
                try
                {
                    fb.Connect();
                    fb.Command("reboot");
                }
                finally
                {
                    fb.Disconnect();
                }
            });
            Log("Rebooting...");
        }
        catch (Exception ex)
        {
            Log($"Reboot Error: {ex.Message}");
        }
    }

    [RelayCommand]
    private async Task ConvertSparseToRawAsync()
    {
        var storage = GetStorage();
        if (storage == null)
        {
            return;
        }

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Sparse Image",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Sparse Image") { Patterns = ["*.img"] }]
        });

        if (result.Count == 0)
        {
            return;
        }

        var inputPath = result[0].Path.LocalPath;

        var saveResult = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Raw Image",
            SuggestedFileName = Path.GetFileNameWithoutExtension(inputPath) + "_raw.img",
            DefaultExtension = ".img"
        });

        if (saveResult == null)
        {
            return;
        }

        var outputPath = saveResult.Path.LocalPath;

        try
        {
            Log($"Starting Sparse to Raw conversion...");
            Log($"Input: {inputPath}");
            Log($"Output: {outputPath}");

            await Task.Run(() => SparseImageConverter.ConvertSparseToRaw([inputPath], outputPath));

            Log("Conversion successful.");
            await Ursa.Controls.MessageBox.ShowOverlayAsync("转换成功！", "提示");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            await Ursa.Controls.MessageBox.ShowOverlayAsync($"转换失败: {ex.Message}", "错误");
        }
    }

    [RelayCommand]
    private async Task ConvertRawToSparseAsync()
    {
        var storage = GetStorage();
        if (storage == null)
        {
            return;
        }

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Raw Image",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Raw Image") { Patterns = ["*.img"] }]
        });

        if (result.Count == 0)
        {
            return;
        }

        var inputPath = result[0].Path.LocalPath;

        var saveResult = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save Sparse Image",
            SuggestedFileName = Path.GetFileNameWithoutExtension(inputPath) + "_sparse.img",
            DefaultExtension = ".img"
        });

        if (saveResult == null)
        {
            return;
        }

        var outputPath = saveResult.Path.LocalPath;

        try
        {
            Log($"Starting Raw to Sparse conversion...");
            Log($"Input: {inputPath}");
            Log($"Output: {outputPath}");

            await Task.Run(() => SparseImageConverter.ConvertRawToSparse(inputPath, outputPath));

            Log("Conversion successful.");
            await Ursa.Controls.MessageBox.ShowOverlayAsync("转换成功！", "提示");
        }
        catch (Exception ex)
        {
            Log($"Error: {ex.Message}");
            await Ursa.Controls.MessageBox.ShowOverlayAsync($"转换失败: {ex.Message}", "错误");
        }
    }

    private IStorageProvider? GetStorage()
    {
        return Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? (desktop.MainWindow?.StorageProvider)
            : null;
    }
}
