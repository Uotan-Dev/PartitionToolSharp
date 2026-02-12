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

    public string FileSystemSizeText
    {
        get
        {
            if (FileSystemType == "Unknown") return "Unformatted";
            if (FileSystemType == "Raw") return "No FS Header";

            if (FileSystemSize > 0)
            {
                return $"({FileSystemSize / (1024 * 1024.0):F2} MiB)";
            }
            return string.Empty;
        }
    }

    public Action? OnChanged { get; set; }

    partial void OnSizeChanged(ulong value) => OnChanged?.Invoke();
    partial void OnNameChanged(string value) => OnChanged?.Invoke();
    partial void OnAttributesChanged(uint value) => OnChanged?.Invoke();

    public string SizeInMiB => $"{Size / (1024 * 1024.0):F2} MiB";
}
