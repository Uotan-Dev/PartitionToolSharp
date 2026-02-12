using System.CommandLine;
using LibLpSharp;
using LibSparseSharp;

var deviceSizeOpt = new Option<string>("--device-size") { Description = "Size of the block device.", IsRequired = true };
deviceSizeOpt.AddAlias("-d");
var metadataSizeOpt = new Option<uint>("--metadata-size") { Description = "Maximum size for partition metadata.", IsRequired = true };
metadataSizeOpt.AddAlias("-m");
var metadataSlotsOpt = new Option<uint>("--metadata-slots") { Description = "Number of slots for metadata.", IsRequired = true };
metadataSlotsOpt.AddAlias("-s");
var outputOpt = new Option<FileInfo>("--output") { Description = "Output file.", IsRequired = true };
outputOpt.AddAlias("-o");
var partitionsOpt = new Option<string[]>("--partition") { Description = "Add a partition (format: name:attr:size[:group])." };
partitionsOpt.AddAlias("-p");
var groupsOpt = new Option<string[]>("--group") { Description = "Define a group (format: name:size)." };
groupsOpt.AddAlias("-g");
var sparseOpt = new Option<bool>("--sparse") { Description = "Output a sparse image." };
sparseOpt.AddAlias("-S");
var imageOpt = new Option<string[]>("--image") { Description = "Include image for partition (format: partition=file)." };
imageOpt.AddAlias("-i");

var rootCommand = new RootCommand("Command-line tool for creating Android Logical Partition images.")
        {
            deviceSizeOpt, metadataSizeOpt, metadataSlotsOpt, outputOpt, partitionsOpt, groupsOpt, sparseOpt, imageOpt
        };

rootCommand.SetHandler((deviceSizeStr, metadataSize, metadataSlots, output, partitions, groups, isSparse, images) =>
{
    try
    {
        var deviceSize = ParseSize(deviceSizeStr);
        var builder = new SuperImageBuilder(deviceSize, metadataSize, metadataSlots);

        if (groups != null)
        {
            foreach (var g in groups)
            {
                var parts = g.Split(':');
                if (parts.Length != 2)
                {
                    throw new Exception($"Invalid group format: {g}");
                }

                builder.AddGroup(parts[0], ParseSize(parts[1]));
            }
        }

        var imageMap = new Dictionary<string, string>();
        if (images != null)
        {
            foreach (var img in images)
            {
                var parts = img.Split('=');
                if (parts.Length != 2)
                {
                    throw new Exception($"Invalid image format: {img}");
                }

                imageMap[parts[0]] = parts[1];
            }
        }

        if (partitions != null)
        {
            foreach (var p in partitions)
            {
                var parts = p.Split(':');
                if (parts.Length < 3)
                {
                    throw new Exception($"Invalid partition format: {p}");
                }

                var name = parts[0];
                var attr = ParseAttributes(parts[1]);
                var size = ParseSize(parts[2]);
                var groupName = parts.Length > 3 ? parts[3] : "default";

                var imagePath = imageMap.TryGetValue(name, out var path) ? path : null;
                builder.AddPartition(name, size, groupName, attr, imagePath);
            }
        }

        Console.WriteLine($"Building image to '{output.FullName}'...");
        if (isSparse)
        {
            using var sparseFile = builder.Build();
            using var fs = output.Create();
            sparseFile.WriteToStream(fs);
        }
        else
        {
            using var sparseFile = builder.Build();
            using var fs = output.Create();

            // 在 Windows 上将其标记为稀疏文件以利用空洞优化
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    SparseFileNativeHelper.MarkAsSparse(fs);
                }
            }
            catch { /* 如果不支持则忽略 */ }

            sparseFile.WriteRawToStream(fs);
        }
        Console.WriteLine("Done.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
    }
}, deviceSizeOpt, metadataSizeOpt, metadataSlotsOpt, outputOpt, partitionsOpt, groupsOpt, sparseOpt, imageOpt);

return await rootCommand.InvokeAsync(args);

ulong ParseSize(string size)
{
    size = size.ToUpper().Trim();
    return size.EndsWith("K")
        ? ulong.Parse(size[..^1]) * 1024
        : size.EndsWith("M")
        ? ulong.Parse(size[..^1]) * 1024 * 1024
        : size.EndsWith("G") ? ulong.Parse(size[..^1]) * 1024 * 1024 * 1024 : ulong.Parse(size);
}

uint ParseAttributes(string attr)
{
    if (attr.Equals("none", StringComparison.OrdinalIgnoreCase))
    {
        return MetadataFormat.LP_PARTITION_ATTR_NONE;
    }

    if (attr.Equals("readonly", StringComparison.OrdinalIgnoreCase))
    {
        return MetadataFormat.LP_PARTITION_ATTR_READONLY;
    }
    // 根据需要添加更多属性
    return 0;
}