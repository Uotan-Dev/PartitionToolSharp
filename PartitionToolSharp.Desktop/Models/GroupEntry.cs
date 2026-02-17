using System;
using CommunityToolkit.Mvvm.ComponentModel;

namespace PartitionToolSharp.Desktop.Models;

public partial class GroupEntry : ObservableObject
{
    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SizeText))]
    private ulong _maxSize;

    public string SizeText => MaxSize == 0 ? "Unlimited" : $"{MaxSize / (1024 * 1024.0):F2} MiB";

    public Action? OnChanged { get; set; }

    partial void OnNameChanged(string value)
    {
        OnChanged?.Invoke();
    }
    partial void OnMaxSizeChanged(ulong value)
    {
        OnChanged?.Invoke();
        OnPropertyChanged(nameof(DisplayMaxSize));
    }

    [ObservableProperty]
    private string _selectedSizeUnit = "MB";

    public double DisplayMaxSize
    {
        get => MaxSize / GetUnitFactor(SelectedSizeUnit);
        set
        {
            MaxSize = (ulong)(value * GetUnitFactor(SelectedSizeUnit));
            OnPropertyChanged(nameof(DisplayMaxSize));
        }
    }

    partial void OnSelectedSizeUnitChanged(string value) => OnPropertyChanged(nameof(DisplayMaxSize));

    private static double GetUnitFactor(string unit) => unit switch
    {
        "KB" => 1024.0,
        "MB" => 1024.0 * 1024.0,
        "GB" => 1024.0 * 1024.0 * 1024.0,
        _ => 1.0
    };
}
