using CommunityToolkit.Mvvm.Messaging.Messages;
using LibLpSharp;

namespace PartitionToolSharp.Desktop.Models;

public class MetadataChangedMessage(LpMetadata? metadata, string? path = null) : ValueChangedMessage<LpMetadata?>(metadata)
{
    public string? Path { get; } = path;
}
