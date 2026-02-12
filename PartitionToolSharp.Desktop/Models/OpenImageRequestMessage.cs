namespace PartitionToolSharp.Desktop.Models;

public class OpenImageRequestMessage 
{ 
    public string? FilePath { get; }
    public OpenImageRequestMessage(string? path = null) => FilePath = path;
}
