using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibLpSharp;
using LibSparseSharp;
using PartitionToolSharp.Desktop.Models;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class PartitionManagerViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<PartitionEntry> _partitions = [];

    [ObservableProperty]
    private PartitionEntry? _selectedPartition;

    [ObservableProperty]
    private string? _currentFilePath;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    private LpMetadata? _metadata;

    public PartitionManagerViewModel()
    {
        // 注册库项目的日志回传
        LpLogger.LogMessage = msg => StatusMessage = msg;
        LpLogger.LogWarning = msg => StatusMessage = $"Warning: {msg}";
        LpLogger.LogError = msg => StatusMessage = $"Error: {msg}";
        SparseLogger.LogMessage = msg => StatusMessage = msg;
    }

    [RelayCommand]
    private async Task OpenFileAsync()
    {
        var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;

        if (topLevel == null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open LP Metadata Image",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Image Files") { Patterns = ["*.img", "*.bin"] }]
        });

        if (files.Count > 0)
        {
            await LoadFileAsync(files[0].Path.LocalPath);
        }
    }

    private async Task LoadFileAsync(string path)
    {
        try
        {
            // 显式清理旧数据并建议 GC 回收，释放前一个文件的内存引用
            Partitions.Clear();
            _metadata = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();

            CurrentFilePath = path;

            _metadata = await Task.Run(() =>
            {
                // 检测是否为 sparse 格式
                using var fs = File.OpenRead(path);
                var magicBuf = new byte[4];
                var isSparse = false;
                if (fs.Read(magicBuf, 0, 4) == 4)
                {
                    if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SPARSE_HEADER_MAGIC)
                    {
                        isSparse = true;
                    }
                }
                fs.Close();

                if (isSparse)
                {
                    // 使用优化过的 FromImageFile，它内部会使用 FileDataProvider 避免内存占用
                    using var sparseFile = SparseFile.FromImageFile(path);
                    using var inputStream = new SparseStream(sparseFile);
                    var result = MetadataReader.ReadFromImageStream(inputStream);
                    return result;
                }
                else
                {
                    // 原生 Raw 镜像直接读取流即可
                    using var inputStream = File.OpenRead(path);
                    var result = MetadataReader.ReadFromImageStream(inputStream);
                    return result;
                }
            });

            if (_metadata == null)
            {
                StatusMessage = "加载失败: 无法解析元数据";
                await Ursa.Controls.MessageBox.ShowOverlayAsync("加载失败: 无法解析元数据 (Metadata 为空)", "错误");
                return;
            }

            // 在UI线程更新集合
            Dispatcher.UIThread.Post(() => {
                Partitions.Clear();
                foreach (var part in _metadata.Partitions)
                {
                    var groupName = "default";
                    if (part.GroupIndex < _metadata.Groups.Count)
                    {
                        groupName = _metadata.Groups[(int)part.GroupIndex].GetName();
                    }

                    // Calculate total size from extents
                    ulong totalSize = 0;
                    for (uint i = 0; i < part.NumExtents; i++)
                    {
                        totalSize += _metadata.Extents[(int)(part.FirstExtentIndex + i)].NumSectors * 512;
                    }

                    var entry = new PartitionEntry
                    {
                        Name = part.GetName(),
                        Group = groupName,
                        Size = totalSize,
                        Attributes = part.Attributes
                    };
                    Partitions.Add(entry);
                }
                StatusMessage = $"已加载 {Partitions.Count} 个分区条目";
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"错误: {ex.Message}";
        }
    }
}
