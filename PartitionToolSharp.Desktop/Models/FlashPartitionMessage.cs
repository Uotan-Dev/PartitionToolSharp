using System.IO;

namespace PartitionToolSharp.Desktop.Models;

public record FlashPartitionMessage(string PartitionName, string? ImagePath, Stream? DataStream = null, long DataLength = 0);
