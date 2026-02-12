using System;

namespace PartitionToolSharp.Desktop.Models;

public class AppSettings
{
    public string? LastOpenedFilePath { get; set; }
    public double WindowWidth { get; set; } = 1200;
    public double WindowHeight { get; set; } = 800;
    public string? Theme { get; set; } = "Light";
}
