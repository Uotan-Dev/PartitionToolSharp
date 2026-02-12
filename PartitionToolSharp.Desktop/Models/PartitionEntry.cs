using CommunityToolkit.Mvvm.ComponentModel;

namespace PartitionToolSharp.Desktop.Models;

public partial class PartitionEntry : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private string _group = string.Empty;

    [ObservableProperty]
    private ulong _size;

    [ObservableProperty]
    private uint _attributes;

    public string SizeInMiB => $"{Size / (1024 * 1024.0):F2} MiB";
}
