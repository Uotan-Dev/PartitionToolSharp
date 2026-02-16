using System.Runtime.InteropServices;
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
        SparseFileNativeHelper.MarkAsSparse(outputStream);
        long maxFileSize = 0;
        foreach (var inputFile in inputFiles)
        {
            var tempSparseFile = SparseFile.FromImageFile(inputFile);
            var fileSize = (long)tempSparseFile.Header.TotalBlocks * tempSparseFile.Header.BlockSize;
            maxFileSize = Math.Max(maxFileSize, fileSize);
            tempSparseFile.Dispose();
        }
        outputStream.SetLength(maxFileSize);
        foreach (var inputFile in inputFiles)
        {
            var sparseFile = SparseFile.FromImageFile(inputFile);
            WriteRawImageFromSparse(sparseFile, outputStream);
            sparseFile.Dispose();
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
        if (System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magicBuf) == SparseFormat.SPARSE_HEADER_MAGIC)
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
        // 已经迁移到 SparseFile 内部实现，这里直接调用并使用 sparseMode=true 以支持 Seek 跳过
        if (outputStream.CanSeek)
        {
            outputStream.Seek(0, SeekOrigin.Begin);
        }
        sparseFile.WriteRawToStream(outputStream, true);
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
        var totalLength = fi.Length;
        long currentPos = 0;
        long rawStart = -1;

        while (currentPos < totalLength)
        {
            fs.Position = currentPos;
            var bytesRead = fs.Read(buffer, 0, (int)Math.Min(blockSize, totalLength - currentPos));
            if (bytesRead == 0)
            {
                break;
            }

            uint fillValue = 0;
            var isZero = IsZeroBlock(buffer, bytesRead);
            var isFill = !isZero && bytesRead == blockSize && IsFillBlock(buffer, out fillValue);

            if (isZero || isFill)
            {
                if (rawStart != -1)
                {
                    sparseFile.AddRawFileChunk(inputPath, rawStart, (uint)(currentPos - rawStart));
                    rawStart = -1;
                }

                if (isZero)
                {
                    var zeroStart = currentPos;
                    currentPos += bytesRead;
                    while (currentPos < totalLength)
                    {
                        var innerRead = fs.Read(buffer, 0, (int)Math.Min(blockSize, totalLength - currentPos));
                        if (innerRead > 0 && IsZeroBlock(buffer, innerRead))
                        {
                            currentPos += innerRead;
                        }
                        else
                        {
                            break;
                        }
                    }
                    sparseFile.AddDontCareChunk((uint)(currentPos - zeroStart));
                }
                else
                {
                    var fillStart = currentPos;
                    var currentFillValue = fillValue;
                    currentPos += bytesRead;
                    while (currentPos < totalLength)
                    {
                        var innerRead = fs.Read(buffer, 0, (int)Math.Min(blockSize, totalLength - currentPos));
                        if (innerRead == blockSize && IsFillBlock(buffer, out var innerFill) && innerFill == currentFillValue)
                        {
                            currentPos += innerRead;
                        }
                        else
                        {
                            break;
                        }
                    }
                    sparseFile.AddFillChunk(currentFillValue, (uint)(currentPos - fillStart));
                }
            }
            else
            {
                if (rawStart == -1)
                {
                    rawStart = currentPos;
                }

                currentPos += bytesRead;
            }
        }

        if (rawStart != -1)
        {
            sparseFile.AddRawFileChunk(inputPath, rawStart, (uint)(currentPos - rawStart));
        }

        return sparseFile;
    }

    private static bool IsZeroBlock(byte[] buffer, int length)
    {
        if (length == 0)
        {
            return true;
        }

        var span = buffer.AsSpan(0, length);
        var ulongSpan = MemoryMarshal.Cast<byte, ulong>(span);
        foreach (var v in ulongSpan)
        {
            if (v != 0)
            {
                return false;
            }
        }

        for (var i = ulongSpan.Length * 8; i < length; i++)
        {
            if (buffer[i] != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsFillBlock(byte[] buffer, out uint fillValue)
    {
        fillValue = 0;
        if (buffer.Length < 4)
        {
            return false;
        }

        var pattern = BitConverter.ToUInt32(buffer, 0);
        var span = buffer.AsSpan();
        var uintSpan = MemoryMarshal.Cast<byte, uint>(span);
        foreach (var v in uintSpan)
        {
            if (v != pattern)
            {
                return false;
            }
        }

        for (var i = uintSpan.Length * 4; i < buffer.Length; i++)
        {
            if (buffer[i] != (byte)(pattern >> (i % 4 * 8)))
            {
                return false;
            }
        }

        fillValue = pattern;
        return true;
    }
}