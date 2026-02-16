using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibSparseSharp;
using LibFastbootSharp;

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

    private Stream? _directStream;
    private long _directStreamLength;

    public ObservableCollection<string> ConnectedDevices { get; } = [];
    public ObservableCollection<string> Logs { get; } = [];

    public void SetDirectStream(Stream stream, long length)
    {
        _directStream?.Dispose();
        _directStream = stream;
        _directStreamLength = length;
        SelectedImagePath = "[直接数据流]";
    }

    public void Log(string message) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}"));

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
        if (storage == null)
        {
            return;
        }

        var result = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Image to Flash",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Image Files") { Patterns = ["*.img", "*.bin"] }]
        });

        if (result.Count > 0)
        {
            _directStream?.Dispose();
            _directStream = null;
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

        if (_directStream == null && (string.IsNullOrEmpty(SelectedImagePath) || !File.Exists(SelectedImagePath)))
        {
            await Ursa.Controls.MessageBox.ShowOverlayAsync("请选择有效的镜像文件或使用联动数据流。", "提示");
            return;
        }

        IsBusy = true;
        Status = "Flashing...";

        Log("========================================");
        Log($"开始刷入操作:");
        Log($"  目标分区: {PartitionName}");
        Log($"  镜像来源: {(_directStream != null ? "[联动数据流]" : SelectedImagePath)}");
        Log($"  目标设备: {SelectedDevice}");
        Log("========================================");

        try
        {
            await Task.Run(() =>
            {
                var fb = new Fastboot(SelectedDevice);
                try
                {
                    Log("> 正在连接设备...");
                    fb.Connect();

                    Log("> 检查设备模式...");
                    var isUserspaceResp = fb.Command("getvar:is-userspace");
                    var isFastbootd = isUserspaceResp.Status == Fastboot.Status.Okay && isUserspaceResp.Payload.Trim() == "yes";

                    if (!PartitionName.Equals("super", StringComparison.OrdinalIgnoreCase) && !isFastbootd)
                    {
                        throw new Exception("刷入逻辑分区需要处于 fastbootd 模式。请在设备上执行 'fastboot reboot fastboot'。");
                    }

                    if (_directStream != null)
                    {
                        Log($"> 正在从流上传数据 ({_directStreamLength / 1024.0 / 1024.0:F2} MiB)...");
                        fb.UploadData(_directStream);
                    }
                    else
                    {
                        Log($"> 正在从文件上传数据 ({new FileInfo(SelectedImagePath).Length / 1024.0 / 1024.0:F2} MiB)...");
                        fb.UploadData(SelectedImagePath);
                    }

                    Log($"> 正在执行刷入命令: flash:{PartitionName} ...");
                    var resp = fb.Command($"flash:{PartitionName}");

                    if (resp.Status == Fastboot.Status.Okay)
                    {
                        Log("OKAY [完成]");
                    }
                    else
                    {
                        Log($"FAIL [失败]: {resp.Payload}");
                        throw new Exception(resp.Payload);
                    }
                }
                finally
                {
                    fb.Disconnect();
                }
            });

            Log("刷入成功！");
            await Ursa.Controls.MessageBox.ShowOverlayAsync("刷入成功！", "提示");
        }
        catch (Exception ex)
        {
            Log($"刷入错误: {ex.Message}");
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
