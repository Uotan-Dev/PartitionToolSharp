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

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class FlasherViewModel : ObservableObject
{
    [ObservableProperty]
    private string _status = "Ready";

    public ObservableCollection<string> Logs { get; } = [];

    private void Log(string message) => Avalonia.Threading.Dispatcher.UIThread.Post(() => Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}"));

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
