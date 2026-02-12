namespace PartitionToolSharp.Desktop.Models;

public class OpenImageRequestMessage(string? path = null)
{
    public string? FilePath { get; } = path;
}
