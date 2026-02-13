using CommunityToolkit.Mvvm.Messaging.Messages;
using LibLpSharp;

namespace PartitionToolSharp.Desktop.Models;

public class MetadataChangedMessage(LpMetadata? metadata, string? path = null) : ValueChangedMessage<LpMetadata?>(metadata)
{
    public string? Path { get; } = path;
    public double? UsagePercentage { get; set; }
    public string? UsageText { get; set; }
    public ulong? ImageSize { get; set; }
}
