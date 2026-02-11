using LibLpSharp;

namespace LibSparseSharp;

/// <summary>
/// Sparse 镜像转换器
/// </summary>
public class SparseImageConverter
{

    /// <summary>
    /// 将sparse镜像转换为原始镜像
    /// </summary>
    public static void ConvertSparseToRaw(string[] inputFiles, string outputFile)
    {
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
        long maxFileSize = 0;
        foreach (var inputFile in inputFiles)
        {
            using var tempStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
            var tempSparseFile = SparseFile.FromStream(tempStream);
            var fileSize = (long)tempSparseFile.Header.TotalBlocks * tempSparseFile.Header.BlockSize;
            maxFileSize = Math.Max(maxFileSize, fileSize);
        }
        outputStream.SetLength(maxFileSize);
        foreach (var inputFile in inputFiles)
        {
            using var inputStream = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
            var sparseFile = SparseFile.FromStream(inputStream);
            WriteRawImageFromSparse(sparseFile, outputStream);
        }
    }

    /// <summary>
    /// 将原始镜像转换为sparse镜像
    /// </summary>
    public static void ConvertRawToSparse(string inputFile, string outputFile, uint blockSize = 4096)
    {
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
        using var sparseFile = CreateSparseFromFileRef(inputFile, blockSize);
        sparseFile.WriteToStream(outputStream);
    }

    /// <summary>
    /// 创建一个新的 Super 镜像并保存为 Sparse 格式
    /// </summary>
    public static void CreateSuperSparse(
        string outputFile,
        ulong deviceSize,
        uint metadataMaxSize,
        uint metadataSlotCount,
        IDictionary<string, (ulong Size, string? ImagePath)> partitions,
        IDictionary<string, ulong>? groups = null)
    {
        var builder = new SuperImageBuilder(deviceSize, metadataMaxSize, metadataSlotCount);

        if (groups != null)
        {
            foreach (var group in groups)
            {
                builder.AddGroup(group.Key, group.Value);
            }
        }

        foreach (var p in partitions)
        {
            builder.AddPartition(p.Key, p.Value.Size, "default", MetadataFormat.LP_PARTITION_ATTR_READONLY, p.Value.ImagePath);
        }

        using var sparseFile = builder.Build();
        using var outputStream = new FileStream(outputFile, FileMode.Create, FileAccess.Write);
        sparseFile.WriteToStream(outputStream);
    }

    /// <summary>
    /// 解包 Super 镜像（支持 Sparse 和 Raw）
    /// </summary>
    public static void UnpackSuper(string inputFile, string outputDir)
    {
        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        using var fs = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
        Stream superStream = fs;

        // 检测是否为 Sparse 格式
        var magicBuf = new byte[4];
        fs.ReadExactly(magicBuf, 0, 4);
        fs.Seek(0, SeekOrigin.Begin);

        SparseFile? sparseFile = null;
        if (BitConverter.ToUInt32(magicBuf, 0) == SparseFormat.SPARSE_HEADER_MAGIC)
        {
            sparseFile = SparseFile.FromStream(fs);
            superStream = new SparseStream(sparseFile);
        }

        try
        {
            var metadata = MetadataReader.ReadFromImageStream(superStream);
            foreach (var partition in metadata.Partitions)
            {
                var name = partition.GetName();
                var outputPath = Path.Combine(outputDir, $"{name}.img");

                // 1. 计算总大小并预设长度
                ulong totalSectors = 0;
                for (var i = 0; i < partition.NumExtents; i++)
                {
                    totalSectors += metadata.Extents[(int)(partition.FirstExtentIndex + i)].NumSectors;
                }

                var totalSize = (long)totalSectors * MetadataFormat.LP_SECTOR_SIZE;

                using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None);
                SparseFileNativeHelper.MarkAsSparse(outFs); // 标记为稀疏文件以支持空洞优化
                outFs.SetLength(totalSize);

                long currentOutOffset = 0;
                for (var i = 0; i < partition.NumExtents; i++)
                {
                    var extent = metadata.Extents[(int)(partition.FirstExtentIndex + i)];
                    var size = (long)extent.NumSectors * MetadataFormat.LP_SECTOR_SIZE;

                    if (extent.TargetType == MetadataFormat.LP_TARGET_TYPE_LINEAR)
                    {
                        var offset = (long)extent.TargetData * MetadataFormat.LP_SECTOR_SIZE;
                        superStream.Seek(offset, SeekOrigin.Begin);
                        outFs.Seek(currentOutOffset, SeekOrigin.Begin); // 定位到输出文件的确切位置
                        CopyStreamPart(superStream, outFs, size);
                    }
                    // 对于 LP_TARGET_TYPE_ZERO，由于已 SetLength 且标记为 Sparse，
                    // 只需跳过（currentOutOffset 增加）即可，文件系统不会分配实际空间。

                    currentOutOffset += size;
                }
            }
        }
        finally
        {
            if (sparseFile != null)
            {
                superStream.Dispose();
                sparseFile.Dispose();
            }
        }
    }

    private static void CopyStreamPart(Stream input, Stream output, long length)
    {
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = input.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            output.Write(buffer, 0, read);
            remaining -= read;
        }
    }

    /// <summary>
    /// 从sparse文件写入原始镜像数据
    /// </summary>
    private static void WriteRawImageFromSparse(SparseFile sparseFile, Stream outputStream)
    {
        var blockSize = sparseFile.Header.BlockSize;
        var currentBlock = 0u;
        var blocksWritten = 0u;

        foreach (var chunk in sparseFile.Chunks)
        {
            var targetPosition = (long)currentBlock * blockSize;
            outputStream.Seek(targetPosition, SeekOrigin.Begin);

            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (chunk.DataProvider != null)
                    {
                        chunk.DataProvider.WriteTo(outputStream);
                        blocksWritten += chunk.Header.ChunkSize;
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_FILL:
                    var fillBytes = BitConverter.GetBytes(chunk.FillValue);
                    var totalFillSize = chunk.Header.ChunkSize * blockSize;
                    blocksWritten += chunk.Header.ChunkSize;

                    for (uint i = 0; i < totalFillSize; i += 4)
                    {
                        var bytesToWrite = Math.Min(4, (int)(totalFillSize - i));
                        outputStream.Write(fillBytes, 0, bytesToWrite);
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                    blocksWritten += chunk.Header.ChunkSize;
                    break;

                case SparseFormat.CHUNK_TYPE_CRC32:
                    break;

                default:
                    throw new InvalidDataException($"未知的 chunk 类型: 0x{chunk.Header.ChunkType:X4}");
            }

            currentBlock += chunk.Header.ChunkSize;
        }
    }

    /// <summary>
    /// 将大的 sparse 镜像拆分为多个指定大小的镜像文件
    /// </summary>
    public static void ResparseImage(string inputFile, string outputPattern, long maxFileSize)
    {
        using var stream = new FileStream(inputFile, FileMode.Open, FileAccess.Read);
        var sparseFile = SparseFile.FromStream(stream);
        var files = sparseFile.Resparse(maxFileSize);

        for (var i = 0; i < files.Count; i++)
        {
            var outPath = outputPattern.Contains("{0}")
                ? string.Format(outputPattern, i)
                : $"{outputPattern}.{i:D2}";

            using var outStream = new FileStream(outPath, FileMode.Create, FileAccess.Write);
            files[i].WriteToStream(outStream, true); // 拆分后的镜像通常需要包含 CRC
        }
    }

    /// <summary>
    /// 从原始文件创建sparse文件，使用文件引用避免大内存占用
    /// </summary>
    private static SparseFile CreateSparseFromFileRef(string inputPath, uint blockSize)
    {
        var fi = new FileInfo(inputPath);
        var sparseFile = new SparseFile(blockSize, fi.Length);

        using var fs = new FileStream(inputPath, FileMode.Open, FileAccess.Read);
        var buffer = new byte[blockSize];
        long currentOffset = 0;

        while (currentOffset < fi.Length)
        {
            fs.Seek(currentOffset, SeekOrigin.Begin);
            var bytesRead = fs.Read(buffer, 0, (int)blockSize);
            if (bytesRead == 0)
            {
                break;
            }

            if (IsZeroBlock(buffer, bytesRead))
            {
                var startOffset = currentOffset;
                currentOffset += bytesRead;
                while (currentOffset < fi.Length)
                {
                    var innerRead = fs.Read(buffer, 0, (int)blockSize);
                    if (innerRead > 0 && IsZeroBlock(buffer, innerRead))
                    {
                        currentOffset += innerRead;
                    }
                    else
                    {
                        break;
                    }
                }
                sparseFile.AddDontCareChunk((uint)(currentOffset - startOffset));
                continue;
            }

            if (bytesRead == blockSize && IsFillBlock(buffer, out var fillValue))
            {
                var startOffset = currentOffset;
                currentOffset += bytesRead;
                while (currentOffset < fi.Length)
                {
                    var innerRead = fs.Read(buffer, 0, (int)blockSize);
                    if (innerRead == blockSize && IsFillBlock(buffer, out var innerFill) && innerFill == fillValue)
                    {
                        currentOffset += innerRead;
                    }
                    else
                    {
                        break;
                    }
                }
                sparseFile.AddFillChunk(fillValue, (uint)(currentOffset - startOffset));
                continue;
            }

            // 原始数据块，记录文件偏移而不是读取内容
            sparseFile.AddRawFileChunk(inputPath, currentOffset, (uint)bytesRead);
            currentOffset += bytesRead;
        }

        return sparseFile;
    }

    /// <summary>
    /// 检查是否为全零块
    /// </summary>
    private static bool IsZeroBlock(byte[] buffer, int length) => buffer.Take(length).All(b => b == 0);

    /// <summary>
    /// 检查是否为填充块（所有字节都相同）
    /// </summary>
    private static bool IsFillBlock(byte[] buffer, out uint fillValue)
    {
        fillValue = 0;

        if (buffer.Length < 4)
        {
            return false;
        }

        var pattern = BitConverter.ToUInt32(buffer, 0);

        for (var i = 4; i < buffer.Length; i += 4)
        {
            var remainingBytes = Math.Min(4, buffer.Length - i);
            var currentPattern = 0u;

            for (var j = 0; j < remainingBytes; j++)
            {
                currentPattern |= (uint)(buffer[i + j] << (j * 8));
            }

            if (remainingBytes == 4 && currentPattern != pattern)
            {
                return false;
            }
            else if (remainingBytes < 4)
            {
                var expectedPattern = pattern & ((1u << (remainingBytes * 8)) - 1);
                if (currentPattern != expectedPattern)
                {
                    return false;
                }
            }
        }

        fillValue = pattern;
        return true;
    }
}