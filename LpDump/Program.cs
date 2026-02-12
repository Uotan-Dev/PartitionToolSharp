using System.CommandLine;
using Android;
using Google.Protobuf;
using LibLpSharp;
using LibSparseSharp;

var superImageArg = new Argument<FileInfo>("super_image") { Description = "Path to the super image file." };
var jsonOption = new Option<bool>("--json", "Output in JSON format.");

var rootCommand = new RootCommand("Command-line tool for dumping Android Logical Partition metadata.")
        {
            superImageArg,
            jsonOption
        };

rootCommand.SetHandler((superImage, useJson) =>
{
    if (!superImage.Exists)
    {
        Console.Error.WriteLine($"Error: File '{superImage.FullName}' does not exist.");
        return;
    }

    try
    {
        using var fs = superImage.OpenRead();
        Stream inputStream = fs;

        // 检测是否为 sparse 格式
        var magicBuf = new byte[4];
        fs.ReadExactly(magicBuf, 0, 4);
        fs.Seek(0, SeekOrigin.Begin);

        SparseFile? sparseFile = null;
        if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SPARSE_HEADER_MAGIC)
        {
            sparseFile = SparseFile.FromStream(fs);
            inputStream = new SparseStream(sparseFile);
        }

        var metadata = MetadataReader.ReadFromImageStream(inputStream);

        if (useJson)
        {
            DumpJson(metadata);
        }
        else
        {
            DumpText(metadata);
        }

        sparseFile?.Dispose();
    }
    catch (InvalidDataException ex) when (ex.Message.Contains("魔数") || ex.Message.Contains("Magic"))
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
        Console.Error.WriteLine("Hint: The image might be a sparse image that failed detection, or it might be raw metadata at an unsupported offset.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Error: {ex.Message}");
    }
}, superImageArg, jsonOption);

return await rootCommand.InvokeAsync(args);

void DumpText(LpMetadata metadata)
{
    Console.WriteLine("Metadata version: {0}.{1}", metadata.Header.MajorVersion, metadata.Header.MinorVersion);
    Console.WriteLine("Metadata size: {0} bytes", metadata.Geometry.MetadataMaxSize);
    Console.WriteLine("Metadata slot count: {0}", metadata.Geometry.MetadataSlotCount);
    Console.WriteLine("Header flags: 0x{0:X}", metadata.Header.Flags);
    Console.WriteLine();

    Console.WriteLine("--- Block Devices ---");
    foreach (var dev in metadata.BlockDevices)
    {
        Console.WriteLine("Device name: {0}", dev.GetPartitionName());
        Console.WriteLine("  Size: {0} bytes", dev.Size);
        Console.WriteLine("  Alignment: {0} bytes", dev.Alignment);
        Console.WriteLine("  Alignment offset: {0} bytes", dev.AlignmentOffset);
        Console.WriteLine("  First logical sector: {0}", dev.FirstLogicalSector);
    }
    Console.WriteLine();

    Console.WriteLine("--- Groups ---");
    foreach (var group in metadata.Groups)
    {
        Console.WriteLine("Group: {0}", group.GetName());
        Console.WriteLine("  Maximum size: {0} bytes", group.MaximumSize);
        Console.WriteLine("  Flags: 0x{0:X}", group.Flags);
    }
    Console.WriteLine();

    Console.WriteLine("--- Partitions ---");
    foreach (var part in metadata.Partitions)
    {
        Console.WriteLine("Partition: {0}", part.GetName());
        Console.WriteLine("  Group: {0}", metadata.Groups[(int)part.GroupIndex].GetName());
        Console.WriteLine("  Attributes: 0x{0:X}", part.Attributes);
        Console.WriteLine("  Extents:");
        for (uint i = 0; i < part.NumExtents; i++)
        {
            var extent = metadata.Extents[(int)(part.FirstExtentIndex + i)];
            if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
            {
                Console.WriteLine("    Linear: {0} sectors at sector {1}", extent.NumSectors, extent.TargetData);
            }
            else
            {
                Console.WriteLine("    Zero: {0} sectors", extent.NumSectors);
            }
        }
    }
}

void DumpJson(LpMetadata metadata)
{
    var proto = new DynamicPartitionsDeviceInfoProto
    {
        Enabled = true
    };

    foreach (var dev in metadata.BlockDevices)
    {
        proto.BlockDevices.Add(new DynamicPartitionsDeviceInfoProto.Types.BlockDevice
        {
            Name = dev.GetPartitionName(),
            Size = dev.Size,
            Alignment = dev.Alignment,
            AlignmentOffset = dev.AlignmentOffset
        });
    }

    foreach (var group in metadata.Groups)
    {
        proto.Groups.Add(new DynamicPartitionsDeviceInfoProto.Types.Group
        {
            Name = group.GetName(),
            MaximumSize = group.MaximumSize
        });
    }

    foreach (var part in metadata.Partitions)
    {
        var p = new DynamicPartitionsDeviceInfoProto.Types.Partition
        {
            Name = part.GetName(),
            GroupName = metadata.Groups[(int)part.GroupIndex].GetName(),
            Size = (ulong)part.NumExtents * 0, // Placeholder
        };

        ulong totalSize = 0;
        for (uint i = 0; i < part.NumExtents; i++)
        {
            totalSize += metadata.Extents[(int)(part.FirstExtentIndex + i)].NumSectors * MetadataFormat.LP_SECTOR_SIZE;
        }
        p.Size = totalSize;
        proto.Partitions.Add(p);
    }

    var formatter = new JsonFormatter(JsonFormatter.Settings.Default.WithIndentation());
    Console.WriteLine(formatter.Format(proto));
}