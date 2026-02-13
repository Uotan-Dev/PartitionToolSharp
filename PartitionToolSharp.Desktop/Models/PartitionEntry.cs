using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PartitionToolSharp.Desktop.Models;

public partial class PartitionEntry : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _group = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeInMiB))]
    private ulong _size;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly))]
    [NotifyPropertyChangedFor(nameof(IsSlot))]
    private uint _attributes;

    public bool IsReadOnly
    {
        get => (Attributes & 1) != 0;
        set
        {
            var newVal = value ? (Attributes | 1) : (Attributes & ~1u);
            if (newVal != Attributes)
            {
                Attributes = newVal;
                OnPropertyChanged(nameof(IsReadOnly));
            }
        }
    }

    public bool IsSlot
    {
        get => (Attributes & 2) != 0;
        set
        {
            var newVal = value ? (Attributes | 2) : (Attributes & ~2u);
            if (newVal != Attributes)
            {
                Attributes = newVal;
                OnPropertyChanged(nameof(IsSlot));
            }
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSystemSizeText))]
    private ulong _fileSystemSize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileSystemSizeText))]
    private string _fileSystemType = "Unknown";

    public string FileSystemSizeText => FileSystemType == "Unknown"
                ? "Unformatted"
                : FileSystemType == "Raw" ? "No FS Header" : FileSystemSize > 0 ? $"({FileSystemSize / (1024 * 1024.0):F2} MiB)" : string.Empty;

    public Action? OnChanged { get; set; }

    partial void OnSizeChanged(ulong value)
    {
        OnChanged?.Invoke();
        OnPropertyChanged(nameof(DisplaySize));
    }
    partial void OnNameChanged(string value) => OnChanged?.Invoke();
    partial void OnAttributesChanged(uint value) => OnChanged?.Invoke();

    public string SizeInMiB => $"{Size / (1024 * 1024.0):F2} MiB";

    public static readonly string[] Units = ["B", "KB", "MB", "GB"];

    [ObservableProperty]
    private string _selectedSizeUnit = "MB";

    public double DisplaySize
    {
        get => Size / GetUnitFactor(SelectedSizeUnit);
        set
        {
            Size = (ulong)(value * GetUnitFactor(SelectedSizeUnit));
            OnPropertyChanged(nameof(DisplaySize));
        }
    }

    partial void OnSelectedSizeUnitChanged(string value) => OnPropertyChanged(nameof(DisplaySize));

    private static double GetUnitFactor(string unit) => unit switch
    {
        "KB" => 1024.0,
        "MB" => 1024.0 * 1024.0,
        "GB" => 1024.0 * 1024.0 * 1024.0,
        _ => 1.0
    };
}
