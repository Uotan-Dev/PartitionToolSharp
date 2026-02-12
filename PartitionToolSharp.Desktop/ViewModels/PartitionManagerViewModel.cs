using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LibLpSharp;
using LibSparseSharp;
using PartitionToolSharp.Desktop.Models;
using PartitionToolSharp.Desktop.Services;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class PartitionManagerViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<PartitionEntry> _partitions = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedPartitionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MovePartitionUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MovePartitionDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractPartitionCommand))]
    private PartitionEntry? _selectedPartition;

    [ObservableProperty]
    private string? _currentFilePath;

    [ObservableProperty]
    private string _statusMessage = "Ready";

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private double _usagePercentage;

    [ObservableProperty]
    private string _usageText = "0% Used";

    public IEnumerable<PartitionEntry> FilteredPartitions => string.IsNullOrWhiteSpace(SearchText)
                ? Partitions
                : Partitions.Where(p => p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredPartitions));

    private LpMetadata? _metadata;

    public PartitionManagerViewModel()
    {
        LpLogger.LogMessage = msg => Dispatcher.UIThread.Post(() => StatusMessage = msg);
        LpLogger.LogWarning = msg => Dispatcher.UIThread.Post(() => StatusMessage = $"Warning: {msg}");
        LpLogger.LogError = msg => Dispatcher.UIThread.Post(() => StatusMessage = $"Error: {msg}");
        SparseLogger.LogMessage = msg => Dispatcher.UIThread.Post(() => StatusMessage = msg);

        Partitions.CollectionChanged += (s, e) => OnPropertyChanged(nameof(FilteredPartitions));

        if (!string.IsNullOrEmpty(ConfigService.Current.LastOpenedFilePath) && File.Exists(ConfigService.Current.LastOpenedFilePath))
        {
            _ = LoadFileAsync(ConfigService.Current.LastOpenedFilePath);
        }
    }

    private void OnMetadataChanged()
    {
        UpdateUsageStats();
        
        // Notify UI that the filtered view needs refreshing
        var current = SelectedPartition;
        OnPropertyChanged(nameof(FilteredPartitions));
        
        // Restore selection if it was lost during collection refresh
        if (current != null && SelectedPartition != current)
        {
            SelectedPartition = current;
        }

        WeakReferenceMessenger.Default.Send(new MetadataChangedMessage(_metadata, CurrentFilePath));
    }

    private void UpdateUsageStats()
    {
        if (_metadata == null)
        {
            return;
        }

        var totalSize = _metadata.BlockDevices[0].Size;
        ulong usedSize = 0;
        foreach (var p in Partitions)
        {
            usedSize += p.Size;
        }

        UsagePercentage = (double)usedSize / totalSize * 100;
        UsageText = $"{UsagePercentage:F1}% Used ({usedSize / (1024 * 1024.0):F2} MiB / {totalSize / (1024 * 1024.0):F2} MiB)";
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelectedPartition()
    {
        if (SelectedPartition == null)
        {
            return;
        }

        Partitions.Remove(SelectedPartition);
        SelectedPartition = null;
        OnMetadataChanged();
        StatusMessage = "Partition removed from list (unsaved)";
    }

    private bool HasSelection => SelectedPartition != null;

    [RelayCommand]
    private void AddNewPartition()
    {
        var name = $"new_partition_{Partitions.Count + 1}";
        var entry = new PartitionEntry
        {
            Name = name,
            Group = "default",
            Size = 0,
            Attributes = 0,
            OnChanged = OnMetadataChanged
        };
        Partitions.Add(entry);
        SelectedPartition = entry;
        OnMetadataChanged();
        StatusMessage = $"Added {name}";
    }

    [RelayCommand(CanExecute = nameof(CanMoveUp))]
    private void MovePartitionUp()
    {
        if (SelectedPartition is not { } p) return;

        var index = Partitions.IndexOf(p);
        if (index > 0)
        {
            Partitions.RemoveAt(index);
            Partitions.Insert(index - 1, p);
            
            // Re-select the item after move
            SelectedPartition = p;
            
            OnMetadataChanged();
            RefreshCommandStates();
            StatusMessage = $"Moved {p.Name} up";
        }
    }

    private bool CanMoveUp => SelectedPartition != null && Partitions.Count > 1 && Partitions.IndexOf(SelectedPartition) > 0;

    [RelayCommand(CanExecute = nameof(CanMoveDown))]
    private void MovePartitionDown()
    {
        if (SelectedPartition is not { } p) return;

        var index = Partitions.IndexOf(p);
        if (index >= 0 && index < Partitions.Count - 1)
        {
            Partitions.RemoveAt(index);
            Partitions.Insert(index + 1, p);

            // Re-select the item after move
            SelectedPartition = p;

            OnMetadataChanged();
            RefreshCommandStates();
            StatusMessage = $"Moved {p.Name} down";
        }
    }

    private bool CanMoveDown => SelectedPartition != null && Partitions.Count > 1 && Partitions.IndexOf(SelectedPartition) < Partitions.Count - 1;

    private void RefreshCommandStates()
    {
        MovePartitionUpCommand.NotifyCanExecuteChanged();
        MovePartitionDownCommand.NotifyCanExecuteChanged();
        DeleteSelectedPartitionCommand.NotifyCanExecuteChanged();
        if (ExtractPartitionCommand is IRelayCommand extract)
        {
            extract.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private async Task ExtractPartitionAsync()
    {
        if (SelectedPartition == null || string.IsNullOrEmpty(CurrentFilePath))
        {
            return;
        }

        var topLevel = Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
        if (topLevel == null)
        {
            return;
        }

        var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = $"Extract {SelectedPartition.Name}",
            SuggestedFileName = $"{SelectedPartition.Name}.img",
            DefaultExtension = ".img"
        });

        if (file == null)
        {
            return;
        }

        try
        {
            StatusMessage = $"Extracting {SelectedPartition.Name}...";
            await Task.Run(async () =>
            {
                using var sourceFs = File.OpenRead(CurrentFilePath);
                Stream? input = null;
                SparseFile? sparse = null;

                var magic = new byte[4];
                sourceFs.ReadExactly(magic, 0, 4);
                sourceFs.Seek(0, SeekOrigin.Begin);
                if (BitConverter.ToUInt32(magic, 0) == SparseFormat.SPARSE_HEADER_MAGIC)
                {
                    sparse = SparseFile.FromImageFile(CurrentFilePath);
                    input = new SparseStream(sparse);
                }
                else
                {
                    input = sourceFs;
                }

                try
                {
                    var lpPart = _metadata?.Partitions.FirstOrDefault(p => p.GetName() == SelectedPartition.Name);
                    if (lpPart == null || lpPart.Value.NumExtents == 0)
                    {
                        return;
                    }

                    using var outFs = await file.OpenWriteAsync();

                    for (uint i = 0; i < lpPart.Value.NumExtents; i++)
                    {
                        var extent = _metadata!.Extents[(int)(lpPart.Value.FirstExtentIndex + i)];
                        if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                        {
                            input.Seek((long)extent.TargetData * 512, SeekOrigin.Begin);
                            var length = (long)extent.NumSectors * 512;
                            var buffer = new byte[1024 * 1024];
                            var remaining = length;
                            while (remaining > 0)
                            {
                                var toRead = (int)Math.Min(buffer.Length, remaining);
                                var read = input.Read(buffer, 0, toRead);
                                if (read <= 0)
                                {
                                    break;
                                }

                                outFs.Write(buffer, 0, read);
                                remaining -= read;
                            }
                        }
                    }
                }
                finally
                {
                    input?.Dispose();
                    sparse?.Dispose();
                }
            });
            StatusMessage = "Extraction successful";
            await Ursa.Controls.MessageBox.ShowOverlayAsync("提取成功！", "提示");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Extraction failed: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task SaveChangesAsync()
    {
        if (string.IsNullOrEmpty(CurrentFilePath) || _metadata == null)
        {
            return;
        }

        try
        {
            StatusMessage = "Saving changes...";

            var builder = MetadataBuilder.FromMetadata(_metadata);

            foreach (var p in Partitions)
            {
                var builderPart = builder.FindPartition(p.Name);
                if (builderPart == null)
                {
                    builder.AddPartition(p.Name, p.Group, p.Attributes);
                    builderPart = builder.FindPartition(p.Name);
                }

                if (builderPart != null)
                {
                    builderPart.Attributes = p.Attributes;
                    builder.ResizePartition(builderPart, p.Size);
                }
            }

            var uiNames = Partitions.Select(p => p.Name).ToList();
            builder.ReorderPartitions(uiNames);

            var newMetadata = builder.Export();

            using (var fs = File.OpenRead(CurrentFilePath))
            {
                var magicBuf = new byte[4];
                fs.ReadExactly(magicBuf, 0, 4);
                if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SPARSE_HEADER_MAGIC)
                {
                    await Ursa.Controls.MessageBox.ShowOverlayAsync("当前版本不支持直接写回 Sparse 镜像，请先转换为 Raw 格式。", "不支持的操作");
                    return;
                }
            }

            using (var fs = new FileStream(CurrentFilePath, FileMode.Open, FileAccess.ReadWrite))
            {
                var geometryData = MetadataWriter.SerializeGeometry(newMetadata.Geometry);
                var metadataData = MetadataWriter.SerializeMetadata(newMetadata);

                fs.Seek(MetadataFormat.LP_PARTITION_RESERVED_BYTES, SeekOrigin.Begin);
                fs.Write(geometryData);
                fs.Write(geometryData);

                var slotOffset = MetadataFormat.LP_PARTITION_RESERVED_BYTES + (MetadataFormat.LP_METADATA_GEOMETRY_SIZE * 2);
                fs.Seek(slotOffset, SeekOrigin.Begin);

                for (var i = 0; i < newMetadata.Geometry.MetadataSlotCount; i++)
                {
                    var paddedMetadata = new byte[newMetadata.Geometry.MetadataMaxSize];
                    Array.Copy(metadataData, paddedMetadata, Math.Min(metadataData.Length, paddedMetadata.Length));
                    fs.Write(paddedMetadata);
                }
            }

            _metadata = newMetadata;
            StatusMessage = "Changes saved successfully to raw image.";
            await Ursa.Controls.MessageBox.ShowOverlayAsync("保存成功！", "提示");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save Error: {ex.Message}";
            await Ursa.Controls.MessageBox.ShowOverlayAsync($"保存失败: {ex.Message}", "错误");
        }
    }

    [RelayCommand]
    public async Task OpenFileAsync()
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

    public async Task LoadFileAsync(string path)
    {
        try
        {
            Partitions.Clear();
            _metadata = null;
            GC.Collect();
            GC.WaitForPendingFinalizers();

            CurrentFilePath = path;
            
            // Save to config
            ConfigService.Current.LastOpenedFilePath = path;
            ConfigService.Save();

            _metadata = await Task.Run(() =>
            {
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
                    using var sparseFile = SparseFile.FromImageFile(path);
                    using var inputStream = new SparseStream(sparseFile);
                    var result = MetadataReader.ReadFromImageStream(inputStream);
                    return result;
                }
                else
                {
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

            Dispatcher.UIThread.Post(() =>
            {
                Partitions.Clear();
                foreach (var part in _metadata.Partitions)
                {
                    var groupName = "default";
                    if (part.GroupIndex < _metadata.Groups.Count)
                    {
                        groupName = _metadata.Groups[(int)part.GroupIndex].GetName();
                    }

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
                        Attributes = part.Attributes,
                        OnChanged = OnMetadataChanged
                    };
                    Partitions.Add(entry);
                }
                OnMetadataChanged();
                StatusMessage = $"已加载 {Partitions.Count} 个分区条目";

                _ = ProbeFileSystemSizesAsync(path);
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"错误: {ex.Message}";
        }
    }

    private async Task ProbeFileSystemSizesAsync(string path)
    {
        try
        {
            await Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                Stream? targetStream = null;
                SparseFile? sparseFile = null;

                var magicBuf = new byte[4];
                fs.ReadExactly(magicBuf, 0, 4);
                fs.Seek(0, SeekOrigin.Begin);

                if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SPARSE_HEADER_MAGIC)
                {
                    sparseFile = SparseFile.FromImageFile(path);
                    targetStream = new SparseStream(sparseFile);
                }
                else
                {
                    targetStream = fs;
                }

                try
                {
                    foreach (var entry in Partitions.ToList())
                    {
                        var lpPart = _metadata?.Partitions.FirstOrDefault(p => p.GetName() == entry.Name);
                        if (lpPart != null && lpPart.Value.NumExtents > 0)
                        {
                            var extent = _metadata!.Extents[(int)lpPart.Value.FirstExtentIndex];
                            if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                            {
                                var offset = extent.TargetData * MetadataFormat.LP_SECTOR_SIZE;
                                var fsSize = Utility.DetectFilesystemSize(targetStream!, offset);

                                Dispatcher.UIThread.Post(() => entry.FileSystemSize = fsSize);
                            }
                        }
                    }
                }
                finally
                {
                    if (sparseFile != null)
                    {
                        targetStream?.Dispose();
                        sparseFile.Dispose();
                    }
                }
            });
            StatusMessage = "文件系统大小探测完成";
        }
        catch
        {
            StatusMessage = "文件系统大小探测失败";
        }
    }
}
