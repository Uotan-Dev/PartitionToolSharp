using System.Text.Json.Serialization;
using PartitionToolSharp.Desktop.Models;

namespace PartitionToolSharp.Desktop.Services;

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(AppSettings))]
internal partial class AppJsonContext : JsonSerializerContext
{
}
