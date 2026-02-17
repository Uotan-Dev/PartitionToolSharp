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
using Ursa.Controls;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class PartitionManagerViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<PartitionEntry> _partitions = [];

    [ObservableProperty]
    private ObservableCollection<GroupEntry> _groups = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedPartitionCommand))]
    [NotifyCanExecuteChangedFor(nameof(MovePartitionUpCommand))]
    [NotifyCanExecuteChangedFor(nameof(MovePartitionDownCommand))]
    [NotifyCanExecuteChangedFor(nameof(ExtractPartitionCommand))]
    [NotifyCanExecuteChangedFor(nameof(FlashSelectedPartitionCommand))]
    private PartitionEntry? _selectedPartition;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteSelectedGroupCommand))]
    private GroupEntry? _selectedGroup;

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

    [ObservableProperty]
    private ulong _imageSize;

    partial void OnImageSizeChanged(ulong value) => OnPropertyChanged(nameof(DisplayImageSize));

    public string[] SizeUnits => PartitionEntry.Units;

    [ObservableProperty]
    private string _imageSizeUnit = "MB";

    public double DisplayImageSize
    {
        get => ImageSize / GetUnitFactor(ImageSizeUnit);
        set
        {
            ImageSize = (ulong)(value * GetUnitFactor(ImageSizeUnit));
            OnPropertyChanged(nameof(DisplayImageSize));
        }
    }

    partial void OnImageSizeUnitChanged(string value) => OnPropertyChanged(nameof(DisplayImageSize));

    private static double GetUnitFactor(string unit) => unit switch
    {
        "KB" => 1024.0,
        "MB" => 1024.0 * 1024.0,
        "GB" => 1024.0 * 1024.0 * 1024.0,
        _ => 1.0
    };

    public IEnumerable<PartitionEntry> FilteredPartitions => string.IsNullOrWhiteSpace(SearchText)
                ? Partitions
                : Partitions.Where(p => p.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase)).ToList();

    public IEnumerable<string> GroupNames => Groups.Select(g => g.Name);

    partial void OnSearchTextChanged(string value) => OnPropertyChanged(nameof(FilteredPartitions));

    private LpMetadata? _metadata;
    private readonly List<LpMetadataExtent> _extentsToWipe = [];

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

    private MetadataBuilder GetUpdatedBuilder()
    {
        if (_metadata == null)
        {
            throw new InvalidOperationException("Metadata is not loaded.");
        }

        var builder = MetadataBuilder.FromMetadata(_metadata);

        // 1. 同步分区组
        var groupsToRemove = builder.Groups
            .Where(bg => bg.Name != "default" && !Groups.Any(vg => vg.Name == bg.Name))
            .Select(bg => bg.Name)
            .ToList();
        foreach (var gn in groupsToRemove)
        {
            builder.RemoveGroup(gn);
        }

        foreach (var g in Groups)
        {
            if (builder.FindGroup(g.Name) == null)
            {
                builder.AddGroup(g.Name, g.MaxSize);
            }
            else
            {
                builder.ResizeGroup(g.Name, g.MaxSize);
            }
        }

        // 2. 移除已经在 UI 上删除的分区
        var uiNamesList = Partitions.Select(p => p.Name).ToList();
        var partitionsToRemove = builder.Partitions
            .Where(bp => !uiNamesList.Contains(bp.Name))
            .Select(bp => bp.Name)
            .ToList();
        foreach (var name in partitionsToRemove)
        {
            builder.RemovePartition(name);
        }

        // 3. 更新或添加分区
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
                builderPart.GroupName = p.Group; // 确保组名同步
                builder.ResizePartition(builderPart, p.Size);
            }
        }

        // 4. 应用重排序
        builder.ReorderPartitions(uiNamesList);

        return builder;
    }

    private void OnMetadataChanged()
    {
        UpdateUsageStats();

        var current = SelectedPartition;
        OnPropertyChanged(nameof(FilteredPartitions));
        OnPropertyChanged(nameof(GroupNames));

        if (current != null && SelectedPartition != current)
        {
            SelectedPartition = current;
        }

        RefreshCommandStates();

        var message = new MetadataChangedMessage(_metadata, CurrentFilePath)
        {
            UsagePercentage = UsagePercentage,
            UsageText = UsageText,
            ImageSize = ImageSize
        };
        WeakReferenceMessenger.Default.Send(message);
    }

    private void UpdateUsageStats()
    {
        if (_metadata == null)
        {
            return;
        }

        var totalSize = ImageSize > 0 ? ImageSize : _metadata.BlockDevices[0].Size;
        ulong usedSize = 0;
        foreach (var p in Partitions)
        {
            usedSize += p.Size;
        }

        UsagePercentage = (double)usedSize / totalSize * 100;
        var totalSizeGb = totalSize / (1024 * 1024 * 1024.0);
        var usedSizeGb = usedSize / (1024 * 1024 * 1024.0);
        UsageText = $"{UsagePercentage:F1}% Used ({usedSizeGb:F2} GB / {totalSizeGb:F2} GB)";
    }

    [RelayCommand]
    private async Task ResizeImageAsync()
    {
        if (_metadata == null)
        {
            return;
        }

        try
        {
            var builder = GetUpdatedBuilder();
            builder.ResizeBlockDevice(ImageSize);
            _metadata = builder.Export();
            OnMetadataChanged();
            StatusMessage = $"镜像大小调整为: {ImageSize / (1024 * 1024.0):F2} MiB";
        }
        catch (Exception ex)
        {
            StatusMessage = $"调整失败: {ex.Message}";
            await MessageBox.ShowOverlayAsync(
                message: ex.Message,
                title: "错误",
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
            // 回滚界面上的 ImageSize
            ImageSize = _metadata.BlockDevices[0].Size;
        }
    }

    [RelayCommand]
    private async Task CompactLayoutAsync()
    {
        if (_metadata == null)
        {
            return;
        }

        try
        {
            var builder = GetUpdatedBuilder();
            builder.CompactPartitions();
            _metadata = builder.Export();
            UpdatePartitionsFromMetadata();
            StatusMessage = "分区布局已紧凑处理";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Compact failed: {ex.Message}";
            await MessageBox.ShowOverlayAsync(
                message: ex.Message,
                title: "错误",
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
        }
    }

    [RelayCommand]
    private async Task ShrinkToFit()
    {
        if (_metadata == null)
        {
            return;
        }

        var result = await MessageBox.ShowOverlayAsync(
            message: "该操作将重新排列所有分区以消除空隙（紧凑布局），从而实现最小镜像体积。是否继续？",
            title: "建议",
            button: MessageBoxButton.YesNo);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var builder = GetUpdatedBuilder();
            builder.CompactPartitions();

            var tempMetadata = builder.Export();
            var maxSectorUsed = tempMetadata.BlockDevices[0].FirstLogicalSector;
            foreach (var extent in tempMetadata.Extents)
            {
                if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                {
                    maxSectorUsed = Math.Max(maxSectorUsed, extent.TargetData + extent.NumSectors);
                }
            }

            var backupSize = (ulong)tempMetadata.Geometry.MetadataMaxSize * tempMetadata.Geometry.MetadataSlotCount;
            var minSize = (maxSectorUsed * MetadataFormat.LP_SECTOR_SIZE) + backupSize;

            var alignment = _metadata.BlockDevices[0].Alignment;
            if (alignment == 0)
            {
                alignment = 4096;
            }

            minSize = (minSize + alignment - 1) / alignment * alignment;

            ImageSize = minSize;

            // 重要：因为我们紧凑了布局，必须应用这个变更，否则后续保存会因为 Offset 超界失败
            _metadata = tempMetadata;
            // 同步回 UI
            UpdatePartitionsFromMetadata();

            StatusMessage = $"建议大小已应用并已完成分区紧凑: {ImageSize / (1024 * 1024.0):F2} MiB";
        }
        catch (Exception ex)
        {
            StatusMessage = $"计算失败: {ex.Message}";
            await MessageBox.ShowOverlayAsync(
                message: $"计算失败: {ex.Message}",
                title: "错误",
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
        }
    }

    private void UpdatePartitionsFromMetadata()
    {
        if (_metadata == null)
        {
            return;
        }

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
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void DeleteSelectedPartition()
    {
        if (SelectedPartition == null)
        {
            return;
        }

        var lpPart = _metadata?.Partitions.FirstOrDefault(p => p.GetName() == SelectedPartition.Name);
        if (lpPart != null)
        {
            for (uint i = 0; i < lpPart.Value.NumExtents; i++)
            {
                var extent = _metadata!.Extents[(int)(lpPart.Value.FirstExtentIndex + i)];
                if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                {
                    _extentsToWipe.Add(extent);
                }
            }
        }

        Partitions.Remove(SelectedPartition);
        SelectedPartition = null;
        OnMetadataChanged();
        StatusMessage = "Partition removed and marked for data wiping on save";
    }

    [RelayCommand]
    private void AddNewGroup()
    {
        var newName = "new_group_" + (Groups.Count + 1);
        var entry = new GroupEntry
        {
            Name = newName,
            MaxSize = 0,
            OnChanged = OnMetadataChanged
        };
        Groups.Add(entry);
        SelectedGroup = entry;
        StatusMessage = $"已添加新分区组: {newName}";
        OnMetadataChanged();
    }

    [RelayCommand]
    private void DeleteSelectedGroup()
    {
        if (SelectedGroup != null)
        {
            if (SelectedGroup.Name == "default")
            {
                StatusMessage = "无法删除 'default' 分区组";
                return;
            }

            if (Partitions.Any(p => p.Group == SelectedGroup.Name))
            {
                StatusMessage = "分区组正在被使用，无法删除";
                return;
            }

            Groups.Remove(SelectedGroup);
            StatusMessage = "分区组已删除";
            OnMetadataChanged();
        }
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
        if (SelectedPartition is not { } p)
        {
            return;
        }

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
        if (SelectedPartition is not { } p)
        {
            return;
        }

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
        FlashSelectedPartitionCommand.NotifyCanExecuteChanged();
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
                if (BitConverter.ToUInt32(magic, 0) == SparseFormat.SparseHeaderMagic)
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
            await MessageBox.ShowOverlayAsync("提取成功！", "提示");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Extraction failed: {ex.Message}";
            await MessageBox.ShowOverlayAsync(
                message: $"提取失败: {ex.Message}",
                title: "错误",
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
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

            var builder = GetUpdatedBuilder();
            builder.ResizeBlockDevice(ImageSize);
            var newMetadata = builder.Export();

            var magicBuf = new byte[4];
            var isSparse = false;
            using (var fs = File.OpenRead(CurrentFilePath))
            {
                if (fs.Read(magicBuf, 0, 4) == 4)
                {
                    if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SparseHeaderMagic)
                    {
                        isSparse = true;
                    }
                }
            }

            if (isSparse)
            {
                await SaveSparseChangesInternalAsync(newMetadata);
            }
            else
            {
                await SaveRawChangesInternalAsync(newMetadata);
            }

            _metadata = newMetadata;
            _extentsToWipe.Clear();
            StatusMessage = "Changes saved successfully.";
            await MessageBox.ShowOverlayAsync("保存成功！", "提示");

            await RefreshProbeAsync();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Save Error: {ex.Message}";
            await MessageBox.ShowOverlayAsync(
                message: $"保存失败: {ex.Message}",
                title: "错误",
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
        }
    }

    private async Task SaveRawChangesInternalAsync(LpMetadata newMetadata)
    {
        await Task.Run(() =>
        {
            MetadataWriter.WriteToImageFile(CurrentFilePath!, newMetadata);

            if (_extentsToWipe.Count > 0)
            {
                using var fs = new FileStream(CurrentFilePath!, FileMode.Open, FileAccess.Write);
                Dispatcher.UIThread.Post(() => StatusMessage = "Wiping deleted partition data...");
                var zeroBuf = new byte[1024 * 1024];
                foreach (var extent in _extentsToWipe)
                {
                    fs.Seek((long)extent.TargetData * 512, SeekOrigin.Begin);
                    var remaining = (long)extent.NumSectors * 512;
                    while (remaining > 0)
                    {
                        var toWrite = (int)Math.Min(zeroBuf.Length, remaining);
                        fs.Write(zeroBuf, 0, toWrite);
                        remaining -= toWrite;
                    }
                }
            }
        });
    }

    private async Task SaveSparseChangesInternalAsync(LpMetadata newMetadata)
    {
        var tempFile = CurrentFilePath + ".tmp";
        await Task.Run(() =>
        {
            using var sourceFs = File.OpenRead(CurrentFilePath!);
            using var sourceSparse = SparseFile.FromStream(sourceFs);
            using var sourceStream = new SparseStream(sourceSparse);

            var superBuilder = new SuperImageBuilder(newMetadata.BlockDevices[0].Size,
                                                     newMetadata.Geometry.MetadataMaxSize,
                                                     newMetadata.Geometry.MetadataSlotCount);

            foreach (var group in newMetadata.Groups)
            {
                superBuilder.AddGroup(group.GetName(), group.MaximumSize);
            }

            foreach (var part in newMetadata.Partitions)
            {
                var name = part.GetName();
                ulong size = 0;
                for (uint i = 0; i < part.NumExtents; i++)
                {
                    size += newMetadata.Extents[(int)(part.FirstExtentIndex + i)].NumSectors * 512;
                }

                // Find old data
                var oldPart = _metadata!.Partitions.FirstOrDefault(p => p.GetName() == name);
                if (oldPart.NumExtents > 0)
                {
                    // Copy data from old first extent (simplification, but usually partitions are contiguous)
                    var oldExtent = _metadata.Extents[(int)oldPart.FirstExtentIndex];
                    if (oldExtent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                    {
                        superBuilder.AddPartition(name, size, "default", part.Attributes, sourceStream, (long)oldExtent.TargetData * 512);
                    }
                    else
                    {
                        superBuilder.AddPartition(name, size, "default", part.Attributes);
                    }
                }
                else
                {
                    superBuilder.AddPartition(name, size, "default", part.Attributes);
                }
            }

            using var newSparseFile = superBuilder.Build();
            using var outFs = File.Create(tempFile);
            newSparseFile.WriteToStream(outFs);
        });

        // Swap files
        File.Delete(CurrentFilePath!);
        File.Move(tempFile, CurrentFilePath!);
    }

    [RelayCommand]
    public async Task RefreshProbeAsync()
    {
        if (string.IsNullOrEmpty(CurrentFilePath))
        {
            return;
        }

        StatusMessage = "正在重新探测文件系统...";
        await ProbeFileSystemSizesAsync(CurrentFilePath);
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
            _extentsToWipe.Clear();
            RefreshCommandStates();
            GC.Collect();
            GC.WaitForPendingFinalizers();

            CurrentFilePath = path;

            ConfigService.Current.LastOpenedFilePath = path;
            ConfigService.Save();

            _metadata = await Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                var magicBuf = new byte[4];
                var isSparse = false;
                if (fs.Read(magicBuf, 0, 4) == 4)
                {
                    if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SparseHeaderMagic)
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
                await MessageBox.ShowOverlayAsync(
                    message: "加载失败: 无法解析元数据 (Metadata 为空)",
                    title: "错误",
                    button: MessageBoxButton.OK,
                    icon: MessageBoxIcon.Error);
                RefreshCommandStates();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                Groups.Clear();
                foreach (var group in _metadata.Groups)
                {
                    var entry = new GroupEntry
                    {
                        Name = group.GetName(),
                        MaxSize = group.MaximumSize,
                        OnChanged = OnMetadataChanged
                    };
                    Groups.Add(entry);
                }

                UpdatePartitionsFromMetadata();

                ImageSize = _metadata.BlockDevices[0].Size;
                StatusMessage = $"已加载 {Partitions.Count} 个分区条目";

                _ = ProbeFileSystemSizesAsync(path);
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"错误: {ex.Message}";
            RefreshCommandStates();
            await MessageBox.ShowOverlayAsync(
                message: $"加载失败: {ex.Message}",
                title: "错误",
                button: MessageBoxButton.OK,
                icon: MessageBoxIcon.Error);
        }
    }

    private async Task ProbeFileSystemSizesAsync(string path)
    {
        try
        {
            // 捕获当前的元数据引用，防止探测过程中被替换导致索引越界
            var currentMetadata = _metadata;
            if (currentMetadata == null) return;

            await Task.Run(() =>
            {
                using var fs = File.OpenRead(path);
                Stream? targetStream = null;
                SparseFile? sparseFile = null;

                var magicBuf = new byte[4];
                fs.ReadExactly(magicBuf, 0, 4);
                fs.Seek(0, SeekOrigin.Begin);

                if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SparseHeaderMagic)
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
                        var lpPart = currentMetadata.Partitions.FirstOrDefault(p => p.GetName() == entry.Name);
                        // LpMetadataPartition 是结构体，找不到会返回 default (NumExtents = 0)
                        if (lpPart.NumExtents > 0)
                        {
                            var extentIdx = (int)lpPart.FirstExtentIndex;
                            if (extentIdx >= 0 && extentIdx < currentMetadata.Extents.Count)
                            {
                                var extent = currentMetadata.Extents[extentIdx];
                                if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                                {
                                    var offset = extent.TargetData * MetadataFormat.LP_SECTOR_SIZE;
                                    var fsInfo = Utility.DetectFilesystem(targetStream!, offset);

                                    Dispatcher.UIThread.Post(() =>
                                    {
                                        entry.FileSystemSize = fsInfo.Size;
                                        entry.FileSystemType = fsInfo.Type;
                                    });
                                }
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

    private PartitionReadStream? CreatePartitionStream(string partitionName)
    {
        if (string.IsNullOrEmpty(CurrentFilePath) || _metadata == null)
        {
            return null;
        }

        var fs = File.OpenRead(CurrentFilePath);
        Stream baseStream;
        var disposables = new List<IDisposable>();

        var magic = new byte[4];
        fs.ReadExactly(magic, 0, 4);
        fs.Seek(0, SeekOrigin.Begin);

        if (BitConverter.ToUInt32(magic, 0) == SparseFormat.SparseHeaderMagic)
        {
            var sparse = SparseFile.FromImageFile(CurrentFilePath);
            disposables.Add(sparse);
            baseStream = new SparseStream(sparse);
        }
        else
        {
            baseStream = fs;
        }

        LpMetadataPartition? lpPart = null;
        foreach (var p in _metadata.Partitions)
        {
            if (p.GetName() == partitionName)
            {
                lpPart = p;
                break;
            }
        }

        if (lpPart == null)
        {
            baseStream.Dispose();
            foreach (var d in disposables)
            {
                d.Dispose();
            }
            return null;
        }

        return new PartitionReadStream(baseStream, _metadata, lpPart.Value, disposables);
    }

    [RelayCommand(CanExecute = nameof(CanFlash))]
    private async Task FlashSelectedPartitionAsync()
    {
        if (SelectedPartition != null)
        {
            try
            {
                var partitionStream = CreatePartitionStream(SelectedPartition.Name);
                if (partitionStream == null)
                {
                    StatusMessage = "准备刷入失败: 找不到分区或元数据无效";
                    return;
                }

                WeakReferenceMessenger.Default.Send(new FlashPartitionMessage(SelectedPartition.Name, null, partitionStream, partitionStream.Length));
            }
            catch (Exception ex)
            {
                StatusMessage = $"准备刷入失败: {ex.Message}";
                await MessageBox.ShowOverlayAsync(
                    message: $"准备刷入失败: {ex.Message}",
                    title: "错误",
                    button: MessageBoxButton.OK,
                    icon: MessageBoxIcon.Error);
            }
        }
        else
        {
            WeakReferenceMessenger.Default.Send(new FlashPartitionMessage("super", CurrentFilePath));
        }
    }

    private bool CanFlash() => _metadata != null;
}
