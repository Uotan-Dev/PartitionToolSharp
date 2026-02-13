using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using LibLpSharp;
using PartitionToolSharp.Desktop.Models;
using PartitionToolSharp.Desktop.Services;

namespace PartitionToolSharp.Desktop.ViewModels;

public partial class DashboardViewModel : ObservableObject, IRecipient<MetadataChangedMessage>
{
    [ObservableProperty]
    private string _deviceSize = "0 GB";

    [ObservableProperty]
    private string _slotCount = "0";

    [ObservableProperty]
    private string _metadataSize = "0 KB";

    [ObservableProperty]
    private double _usagePercentage;

    [ObservableProperty]
    private string _usageText = "0% Used";

    [ObservableProperty]
    private string? _currentPath;

    public ObservableCollection<string> RecentFiles { get; } = [];

    public ObservableCollection<GroupInfo> Groups { get; } = [];

    public DashboardViewModel()
    {
        WeakReferenceMessenger.Default.Register(this);

        foreach (var file in ConfigService.Current.RecentFiles)
        {
            RecentFiles.Add(file);
        }
    }

    public void Receive(MetadataChangedMessage message)
    {
        if (message.Path != null)
        {
            CurrentPath = message.Path;
            if (RecentFiles.Contains(message.Path))
            {
                RecentFiles.Remove(message.Path);
            }

            RecentFiles.Insert(0, message.Path);
            while (RecentFiles.Count > 5)
            {
                RecentFiles.RemoveAt(RecentFiles.Count - 1);
            }

            ConfigService.Current.RecentFiles = [.. RecentFiles];
            ConfigService.Save();
        }
        UpdateStats(message.Value);

        if (message.UsagePercentage.HasValue)
        {
            UsagePercentage = message.UsagePercentage.Value;
        }
        if (message.UsageText != null)
        {
            UsageText = message.UsageText;
        }

        if (message.ImageSize.HasValue)
        {
            var totalSizeGb = message.ImageSize.Value / (1024 * 1024 * 1024.0);
            DeviceSize = $"{totalSizeGb:F2} GB";
        }
    }

    [RelayCommand]
    private void RequestOpenImage() => WeakReferenceMessenger.Default.Send(new OpenImageRequestMessage());

    [RelayCommand]
    private void OpenRecentFile(string path) => WeakReferenceMessenger.Default.Send(new OpenImageRequestMessage(path));

    public void UpdateStats(LpMetadata? metadata)
    {
        if (metadata == null)
        {
            return;
        }

        Groups.Clear();
        foreach (var group in metadata.Groups)
        {
            Groups.Add(new GroupInfo
            {
                Name = group.GetName(),
                SizeText = group.MaximumSize == 0 ? "Unlimited" : $"{group.MaximumSize / (1024 * 1024.0):F2} MiB",
                FlagsText = group.Flags == 0 ? "Default" : $"Flags: 0x{group.Flags:X}"
            });
        }

        var totalSize = metadata.BlockDevices[0].Size;
        var totalSizeGb = totalSize / (1024 * 1024 * 1024.0);
        DeviceSize = $"{totalSizeGb:F2} GB";
        SlotCount = metadata.Geometry.MetadataSlotCount.ToString();
        MetadataSize = $"{metadata.Geometry.MetadataMaxSize / 1024.0:F0} KB";

        ulong usedSectors = 0;
        foreach (var extent in metadata.Extents)
        {
            if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
            {
                usedSectors += extent.NumSectors;
            }
        }

        var usedBytes = usedSectors * 512;
        var usedBytesGb = usedBytes / (1024 * 1024 * 1024.0);
        UsagePercentage = (double)usedBytes / totalSize * 100;
        UsageText = $"{UsagePercentage:F1}% Used ({usedBytesGb:F2} GB / {DeviceSize})";
    }
}

public class GroupInfo
{
    public string Name { get; set; } = "";
    public string SizeText { get; set; } = "";
    public string FlagsText { get; set; } = "";
}
