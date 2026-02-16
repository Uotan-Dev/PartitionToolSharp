using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace LibSparseSharp;

public interface ISparseDataProvider : IDisposable
{
    long Length { get; }
    void WriteTo(Stream stream);
    int Read(long offset, byte[] buffer, int bufferOffset, int count);
    ISparseDataProvider GetSubProvider(long offset, long length);
}

public class MemoryDataProvider(byte[] data, int offset = 0, int length = -1) : ISparseDataProvider
{
    private readonly int _offset = offset;
    private readonly int _length = length < 0 ? data.Length - offset : length;

    public long Length => _length;
    public void WriteTo(Stream stream) => stream.Write(data, _offset, _length);
    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        var available = (int)Math.Max(0, _length - offset);
        var toCopy = Math.Min(count, available);
        if (toCopy <= 0)
        {
            return 0;
        }

        Array.Copy(data, _offset + (int)offset, buffer, bufferOffset, toCopy);
        return toCopy;
    }

    public ISparseDataProvider GetSubProvider(long offset, long length) => new MemoryDataProvider(data, _offset + (int)offset, (int)length);

    public void Dispose() { }
}

public class FileDataProvider(string filePath, long offset, long length) : ISparseDataProvider
{
    public long Length => length;
    public void WriteTo(Stream stream)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = fs.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            stream.Write(buffer, 0, read);
            remaining -= read;
        }
    }
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count)
    {
        if (inOffset >= length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, length - inOffset);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset + inOffset, SeekOrigin.Begin);
        return fs.Read(buffer, bufferOffset, toRead);
    }

    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new FileDataProvider(filePath, offset + subOffset, subLength);

    public void Dispose() { }
}

public class StreamDataProvider(Stream stream, long offset, long length, bool leaveOpen = true) : ISparseDataProvider
{
    public long Length => length;
    public void WriteTo(Stream outStream)
    {
        if (stream.CanSeek)
        {
            stream.Seek(offset, SeekOrigin.Begin);
        }
        var buffer = new byte[1024 * 1024];
        var remaining = length;
        while (remaining > 0)
        {
            var toRead = (int)Math.Min(buffer.Length, remaining);
            var read = stream.Read(buffer, 0, toRead);
            if (read == 0)
            {
                break;
            }

            outStream.Write(buffer, 0, read);
            remaining -= read;
        }
    }
    public int Read(long inOffset, byte[] buffer, int bufferOffset, int count)
    {
        if (inOffset >= length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, length - inOffset);
        if (stream.CanSeek)
        {
            stream.Seek(offset + inOffset, SeekOrigin.Begin);
        }
        return stream.Read(buffer, bufferOffset, toRead);
    }

    public ISparseDataProvider GetSubProvider(long subOffset, long subLength) => new StreamDataProvider(stream, offset + subOffset, subLength, true);

    public void Dispose()
    {
        if (!leaveOpen)
        {
            stream.Dispose();
        }
    }
}

public enum SparseReadMode
{
    Normal = 0,
    Sparse = 1,
    Hole = 2
}

public class SparseChunk(ChunkHeader header) : IDisposable
{
    public uint StartBlock { get; set; }
    public ChunkHeader Header { get; set; } = header;
    public ISparseDataProvider? DataProvider { get; set; }
    public uint FillValue { get; set; }

    public void Dispose() => DataProvider?.Dispose();
}

public class SparseFile : IDisposable
{
    public static SparseHeader PeekHeader(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        var headerData = new byte[SparseFormat.SPARSE_HEADER_SIZE];
        return stream.Read(headerData, 0, headerData.Length) != headerData.Length
            ? throw new InvalidDataException("无法读取 sparse 文件头")
            : SparseHeader.FromBytes(headerData);
    }

    public SparseHeader Header { get; set; }
    public List<SparseChunk> Chunks { get; set; } = [];
    public bool Verbose { get; set; } = false;

    /// <summary>
    /// 获取当前文件已添加的数据块总数（实际覆盖的最大范围）
    /// </summary>
    public uint CurrentBlock
    {
        get
        {
            if (Chunks.Count == 0)
            {
                return 0;
            }
            var last = Chunks.MaxBy(c => c.StartBlock);
            return last!.StartBlock + last.Header.ChunkSize;
        }
    }

    public SparseFile() { }

    public SparseFile(uint blockSize, long totalSize, bool verbose = false)
    {
        Verbose = verbose;
        var totalBlocks = (uint)((totalSize + blockSize - 1) / blockSize);
        Header = new SparseHeader
        {
            Magic = SparseFormat.SPARSE_HEADER_MAGIC,
            MajorVersion = 1,
            MinorVersion = 0,
            FileHeaderSize = SparseFormat.SPARSE_HEADER_SIZE,
            ChunkHeaderSize = SparseFormat.CHUNK_HEADER_SIZE,
            BlockSize = blockSize,
            TotalBlocks = totalBlocks,
            TotalChunks = 0,
            ImageChecksum = 0
        };
    }

    public static SparseFile FromStream(Stream stream, bool validateCrc = false, bool verbose = false) => FromStreamInternal(stream, null, validateCrc, verbose);

    public static SparseFile FromBuffer(byte[] buffer, bool validateCrc = false, bool verbose = false)
    {
        using var ms = new MemoryStream(buffer);
        return FromStream(ms, validateCrc, verbose);
    }

    public static SparseFile FromImageFile(string filePath, bool validateCrc = false, bool verbose = false)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FromStreamInternal(stream, filePath, validateCrc, verbose);
    }

    private static SparseFile FromStreamInternal(Stream stream, string? filePath, bool validateCrc, bool verbose = false)
    {
        var sparseFile = new SparseFile { Verbose = verbose };
        var headerData = new byte[SparseFormat.SPARSE_HEADER_SIZE];
        if (stream.Read(headerData, 0, headerData.Length) != headerData.Length)
        {
            throw new InvalidDataException("无法读取 sparse 文件头");
        }

        sparseFile.Header = SparseHeader.FromBytes(headerData);

        if (verbose)
        {
            SparseLogger.Info($"正在解析 Sparse 镜像 Header: BlockSize={sparseFile.Header.BlockSize}, TotalBlocks={sparseFile.Header.TotalBlocks}, TotalChunks={sparseFile.Header.TotalChunks}");
        }

        if (!sparseFile.Header.IsValid())
        {
            throw new InvalidDataException("无效的 sparse 文件头");
        }

        if (sparseFile.Header.FileHeaderSize > SparseFormat.SPARSE_HEADER_SIZE)
        {
            stream.Seek(sparseFile.Header.FileHeaderSize - SparseFormat.SPARSE_HEADER_SIZE, SeekOrigin.Current);
        }

        var checksum = Crc32.Begin();
        var buffer = new byte[1024 * 1024];
        uint currentBlock = 0;

        for (uint i = 0; i < sparseFile.Header.TotalChunks; i++)
        {
            var chunkHeaderData = new byte[SparseFormat.CHUNK_HEADER_SIZE];
            if (stream.Read(chunkHeaderData, 0, chunkHeaderData.Length) != chunkHeaderData.Length)
            {
                throw new InvalidDataException($"无法读取第 {i} 个 chunk 头");
            }

            var chunkHeader = ChunkHeader.FromBytes(chunkHeaderData);

            if (verbose)
            {
                SparseLogger.Info($"Chunk #{i}: 类型=0x{chunkHeader.ChunkType:X4}, 大小={chunkHeader.ChunkSize} blocks, 总字节流大小={chunkHeader.TotalSize}");
            }

            if (sparseFile.Header.ChunkHeaderSize > SparseFormat.CHUNK_HEADER_SIZE)
            {
                stream.Seek(sparseFile.Header.ChunkHeaderSize - SparseFormat.CHUNK_HEADER_SIZE, SeekOrigin.Current);
            }

            var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlock };

            if (!chunkHeader.IsValid())
            {
                throw new InvalidDataException($"第 {i} 个 chunk 头无效: 类型 0x{chunkHeader.ChunkType:X4}");
            }

            var dataSize = (long)chunkHeader.TotalSize - sparseFile.Header.ChunkHeaderSize;
            var expectedRawSize = (long)chunkHeader.ChunkSize * sparseFile.Header.BlockSize;

            switch (chunkHeader.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (dataSize != expectedRawSize)
                    {
                        throw new InvalidDataException($"第 {i} 个 RAW chunk 的总大小 ({chunkHeader.TotalSize}) 与预期数据大小 ({expectedRawSize}) 不匹配");
                    }

                    if (validateCrc)
                    {
                        var remaining = dataSize;
                        while (remaining > 0)
                        {
                            var toRead = (int)Math.Min(buffer.Length, remaining);
                            if (stream.Read(buffer, 0, toRead) != toRead)
                            {
                                throw new InvalidDataException($"无法读取第 {i} 个 chunk 的原始数据以进行校验");
                            }
                            checksum = Crc32.Update(checksum, buffer, 0, toRead);
                            remaining -= toRead;
                        }

                        if (filePath != null)
                        {
                            chunk.DataProvider = new FileDataProvider(filePath, stream.Position - dataSize, dataSize);
                        }
                        else
                        {
                            stream.Seek(-dataSize, SeekOrigin.Current);
                            var rawData = new byte[dataSize];
                            if (stream.Read(rawData, 0, (int)dataSize) != (int)dataSize)
                            {
                                throw new InvalidDataException($"校验后无法重新读取第 {i} 个 chunk 的数据");
                            }
                            chunk.DataProvider = new MemoryDataProvider(rawData);
                        }
                    }
                    else if (filePath != null)
                    {
                        chunk.DataProvider = new FileDataProvider(filePath, stream.Position, dataSize);
                        stream.Seek(dataSize, SeekOrigin.Current);
                    }
                    else
                    {
                        if (dataSize > int.MaxValue)
                        {
                            throw new NotSupportedException($"第 {i} 个 chunk 的原始数据太大 ({dataSize} 字节)，超过了内存缓冲区限制。");
                        }
                        var rawData = new byte[dataSize];
                        if (stream.Read(rawData, 0, (int)dataSize) != (int)dataSize)
                        {
                            throw new InvalidDataException($"无法读取第 {i} 个 chunk 的原始数据");
                        }
                        chunk.DataProvider = new MemoryDataProvider(rawData);
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_FILL:
                    if (dataSize < 4)
                    {
                        throw new InvalidDataException($"第 {i} 个 FILL chunk 的数据大小 ({dataSize}) 小于 4 字节");
                    }

                    var fillData = new byte[4];
                    if (stream.Read(fillData, 0, 4) != 4)
                    {
                        throw new InvalidDataException($"无法读取第 {i} 个 chunk 的填充值");
                    }

                    chunk.FillValue = BinaryPrimitives.ReadUInt32LittleEndian(fillData);

                    if (validateCrc)
                    {
                        var valBytes = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(valBytes, chunk.FillValue);
                        for (var j = 0; j <= buffer.Length - 4; j += 4)
                        {
                            Array.Copy(valBytes, 0, buffer, j, 4);
                        }

                        var remainingFilling = expectedRawSize;
                        while (remainingFilling > 0)
                        {
                            var toProcess = (int)Math.Min(buffer.Length, remainingFilling);
                            checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                            remainingFilling -= toProcess;
                        }
                    }

                    if (dataSize > 4)
                    {
                        stream.Seek(dataSize - 4, SeekOrigin.Current);
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                    if (validateCrc)
                    {
                        Array.Clear(buffer, 0, buffer.Length);
                        var remainingDontCare = expectedRawSize;
                        while (remainingDontCare > 0)
                        {
                            var toProcess = (int)Math.Min(buffer.Length, remainingDontCare);
                            checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                            remainingDontCare -= toProcess;
                        }
                    }
                    if (dataSize > 0)
                    {
                        stream.Seek(dataSize, SeekOrigin.Current);
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_CRC32:
                    if (dataSize >= 4)
                    {
                        var crcFileData = new byte[4];
                        if (stream.Read(crcFileData, 0, 4) != 4)
                        {
                            throw new InvalidDataException($"无法读取第 {i} 个 chunk 的 CRC32 值");
                        }
                        var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcFileData);
                        if (validateCrc && fileCrc != Crc32.Finish(checksum))
                        {
                            throw new InvalidDataException($"CRC32 校验失败: 文件中为 0x{fileCrc:X8}, 计算得到 0x{Crc32.Finish(checksum):X8}");
                        }
                        if (dataSize > 4)
                        {
                            stream.Seek(dataSize - 4, SeekOrigin.Current);
                        }
                    }
                    break;

                default:
                    throw new InvalidDataException($"第 {i} 个 chunk 类型未知: 0x{chunkHeader.ChunkType:X4}");
            }

            if (chunkHeader.ChunkType != SparseFormat.CHUNK_TYPE_CRC32)
            {
                sparseFile.Chunks.Add(chunk);
                currentBlock += chunkHeader.ChunkSize;
            }
        }

        uint actualTotalBlocks = 0;
        foreach (var chunk in sparseFile.Chunks)
        {
            actualTotalBlocks += chunk.Header.ChunkSize;
        }

        if (verbose)
        {
            SparseLogger.Info($"Sparse 镜像解析完成，共统计到 {sparseFile.Chunks.Count} 个数据块，总计 {actualTotalBlocks} Blocks");
        }

        return sparseFile.Header.TotalBlocks != actualTotalBlocks
            ? throw new InvalidDataException($"块数不匹配: Sparse 表头声明 {sparseFile.Header.TotalBlocks} 块, 但实际解析到 {actualTotalBlocks} 块")
            : sparseFile;
    }

    /// <summary>
    /// 将sparse文件写入流（对齐 libsparse 的 sparse_file_write）
    /// </summary>
    public void WriteToStream(Stream stream, bool sparse = true, bool gzip = false, bool includeCrc = false)
    {
        if (!sparse)
        {
            WriteRawToStream(stream);
            return;
        }

        var targetStream = stream;
        if (gzip)
        {
            targetStream = new System.IO.Compression.GZipStream(stream, System.IO.Compression.CompressionMode.Compress, true);
        }

        try
        {
            // 预处理：合并/填补间隙
            var sortedChunks = Chunks.OrderBy(c => c.StartBlock).ToList();
            var finalChunks = new List<SparseChunk>();
            uint currentBlock = 0;

            foreach (var chunk in sortedChunks)
            {
                if (chunk.StartBlock > currentBlock)
                {
                    // 填补间隙
                    var gapBlocks = chunk.StartBlock - currentBlock;
                    finalChunks.Add(new SparseChunk(new ChunkHeader
                    {
                        ChunkType = SparseFormat.CHUNK_TYPE_DONT_CARE,
                        ChunkSize = gapBlocks,
                        TotalSize = SparseFormat.CHUNK_HEADER_SIZE
                    })
                    { StartBlock = currentBlock });
                }
                finalChunks.Add(chunk);
                currentBlock = chunk.StartBlock + chunk.Header.ChunkSize;
            }

            var outHeader = Header;
            var sumBlocks = currentBlock;

            var needsTrailingSkip = outHeader.TotalBlocks > sumBlocks;

            var totalChunks = (uint)finalChunks.Count;
            if (needsTrailingSkip)
            {
                totalChunks++;
            }
            if (includeCrc)
            {
                totalChunks++;
            }

            outHeader.TotalChunks = totalChunks;
            if (sumBlocks > outHeader.TotalBlocks)
            {
                outHeader.TotalBlocks = sumBlocks;
            }

            var headerData = outHeader.ToBytes();
            targetStream.Write(headerData, 0, headerData.Length);

            var checksum = Crc32.Begin();
            var buffer = new byte[1024 * 1024];

            foreach (var chunk in finalChunks)
            {
                var chunkHeaderData = chunk.Header.ToBytes();
                targetStream.Write(chunkHeaderData, 0, chunkHeaderData.Length);

                var expectedDataSize = (long)chunk.Header.ChunkSize * outHeader.BlockSize;

                switch (chunk.Header.ChunkType)
                {
                    case SparseFormat.CHUNK_TYPE_RAW:
                        if (chunk.DataProvider != null)
                        {
                            long providerOffset = 0;
                            while (providerOffset < chunk.DataProvider.Length)
                            {
                                var toRead = (int)Math.Min(buffer.Length, chunk.DataProvider.Length - providerOffset);
                                var read = chunk.DataProvider.Read(providerOffset, buffer, 0, toRead);
                                if (read <= 0)
                                {
                                    break;
                                }

                                targetStream.Write(buffer, 0, read);
                                if (includeCrc)
                                {
                                    checksum = Crc32.Update(checksum, buffer, 0, read);
                                }
                                providerOffset += read;
                            }

                            var padding = expectedDataSize - providerOffset;
                            if (padding > 0)
                            {
                                Array.Clear(buffer, 0, (int)Math.Min(buffer.Length, padding));
                                while (padding > 0)
                                {
                                    var toWrite = (int)Math.Min(buffer.Length, padding);
                                    targetStream.Write(buffer, 0, toWrite);
                                    if (includeCrc)
                                    {
                                        checksum = Crc32.Update(checksum, buffer, 0, toWrite);
                                    }
                                    padding -= toWrite;
                                }
                            }
                        }
                        else
                        {
                            Array.Clear(buffer, 0, buffer.Length);
                            var remaining = expectedDataSize;
                            while (remaining > 0)
                            {
                                var toWrite = (int)Math.Min(buffer.Length, remaining);
                                targetStream.Write(buffer, 0, toWrite);
                                if (includeCrc)
                                {
                                    checksum = Crc32.Update(checksum, buffer, 0, toWrite);
                                }
                                remaining -= toWrite;
                            }
                        }
                        break;

                    case SparseFormat.CHUNK_TYPE_FILL:
                        var fillValData = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(fillValData, chunk.FillValue);
                        targetStream.Write(fillValData, 0, fillValData.Length);
                        if (includeCrc)
                        {
                            var valBytes = new byte[4];
                            BinaryPrimitives.WriteUInt32LittleEndian(valBytes, chunk.FillValue);
                            for (var i = 0; i <= buffer.Length - 4; i += 4)
                            {
                                Array.Copy(valBytes, 0, buffer, i, 4);
                            }

                            long processed = 0;
                            while (processed < expectedDataSize)
                            {
                                var toProcess = (int)Math.Min(buffer.Length, expectedDataSize - processed);
                                checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                                processed += toProcess;
                            }
                        }
                        break;

                    case SparseFormat.CHUNK_TYPE_DONT_CARE:
                        if (includeCrc)
                        {
                            Array.Clear(buffer, 0, buffer.Length);
                            long processed = 0;
                            while (processed < expectedDataSize)
                            {
                                var toProcess = (int)Math.Min(buffer.Length, expectedDataSize - processed);
                                checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                                processed += toProcess;
                            }
                        }
                        break;

                    case SparseFormat.CHUNK_TYPE_CRC32:
                        break;
                    default:
                        break;
                }
            }

            if (needsTrailingSkip)
            {
                var skipSize = outHeader.TotalBlocks - sumBlocks;
                var skipHeader = new ChunkHeader
                {
                    ChunkType = SparseFormat.CHUNK_TYPE_DONT_CARE,
                    Reserved = 0,
                    ChunkSize = skipSize,
                    TotalSize = SparseFormat.CHUNK_HEADER_SIZE
                };
                targetStream.Write(skipHeader.ToBytes(), 0, SparseFormat.CHUNK_HEADER_SIZE);

                if (includeCrc)
                {
                    var skipDataLen = (long)skipSize * outHeader.BlockSize;
                    Array.Clear(buffer, 0, buffer.Length);
                    long processed = 0;
                    while (processed < skipDataLen)
                    {
                        var toProcess = (int)Math.Min(buffer.Length, skipDataLen - processed);
                        checksum = Crc32.Update(checksum, buffer, 0, toProcess);
                        processed += toProcess;
                    }
                }
            }

            if (includeCrc)
            {
                var finalChecksum = Crc32.Finish(checksum);
                var crcHeader = new ChunkHeader
                {
                    ChunkType = SparseFormat.CHUNK_TYPE_CRC32,
                    Reserved = 0,
                    ChunkSize = 0,
                    TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4
                };
                var finalCrcData = new byte[4];
                BinaryPrimitives.WriteUInt32LittleEndian(finalCrcData, finalChecksum);
                targetStream.Write(crcHeader.ToBytes(), 0, SparseFormat.CHUNK_HEADER_SIZE);
                targetStream.Write(finalCrcData, 0, 4);

                outHeader.ImageChecksum = finalChecksum;
                if (targetStream.CanSeek)
                {
                    var currentPos = targetStream.Position;
                    targetStream.Seek(0, SeekOrigin.Begin);
                    targetStream.Write(outHeader.ToBytes(), 0, SparseFormat.SPARSE_HEADER_SIZE);
                    targetStream.Seek(currentPos, SeekOrigin.Begin);
                }
            }
        }
        finally
        {
            if (gzip)
            {
                targetStream.Dispose();
            }
        }
    }

    public void WriteRawToStream(Stream stream, bool sparseMode = false)
    {
        var buffer = new byte[1024 * 1024];
        foreach (var chunk in Chunks)
        {
            var size = (long)chunk.Header.ChunkSize * Header.BlockSize;
            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (chunk.DataProvider != null)
                    {
                        long written = 0;
                        while (written < chunk.DataProvider.Length)
                        {
                            var toRead = (int)Math.Min(buffer.Length, chunk.DataProvider.Length - written);
                            var read = chunk.DataProvider.Read(written, buffer, 0, toRead);
                            if (read <= 0)
                            {
                                break;
                            }

                            stream.Write(buffer, 0, read);
                            written += read;
                        }
                        if (written < size)
                        {
                            Array.Clear(buffer, 0, (int)Math.Min(buffer.Length, size - written));
                            while (written < size)
                            {
                                var toWrite = (int)Math.Min(buffer.Length, size - written);
                                stream.Write(buffer, 0, toWrite);
                                written += toWrite;
                            }
                        }
                    }
                    else
                    {
                        if (sparseMode && stream.CanSeek)
                        {
                            stream.Seek(size, SeekOrigin.Current);
                        }
                        else
                        {
                            Array.Clear(buffer, 0, buffer.Length);
                            var remaining = size;
                            while (remaining > 0)
                            {
                                var toWrite = (int)Math.Min(buffer.Length, remaining);
                                stream.Write(buffer, 0, toWrite);
                                remaining -= toWrite;
                            }
                        }
                    }
                    break;
                case SparseFormat.CHUNK_TYPE_FILL:
                    var fillValBytes = new byte[4];
                    BinaryPrimitives.WriteUInt32LittleEndian(fillValBytes, chunk.FillValue);
                    for (var i = 0; i <= buffer.Length - 4; i += 4)
                    {
                        Array.Copy(fillValBytes, 0, buffer, i, 4);
                    }

                    var fillRemaining = size;
                    while (fillRemaining > 0)
                    {
                        var toWrite = (int)Math.Min(buffer.Length, fillRemaining);
                        stream.Write(buffer, 0, toWrite);
                        fillRemaining -= toWrite;
                    }
                    break;
                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                    if (sparseMode && stream.CanSeek)
                    {
                        stream.Seek(size, SeekOrigin.Current);
                    }
                    else
                    {
                        Array.Clear(buffer, 0, buffer.Length);
                        var remaining = size;
                        while (remaining > 0)
                        {
                            var toWrite = (int)Math.Min(buffer.Length, remaining);
                            stream.Write(buffer, 0, toWrite);
                            remaining -= toWrite;
                        }
                    }
                    break;
                default:
                    break;
            }
        }

        var expectedFullLength = (long)Header.TotalBlocks * Header.BlockSize;
        if (stream.CanSeek && stream.Length < expectedFullLength)
        {
            stream.SetLength(expectedFullLength);
        }
    }

    /// <summary>
    /// 获取指定的块起始索引，并检查是否重叠
    /// </summary>
    private uint GetNextBlockAndCheckOverlap(uint? blockIndex, uint sizeInBlocks)
    {
        var start = blockIndex ?? CurrentBlock;
        foreach (var chunk in Chunks)
        {
            if (start < chunk.StartBlock + chunk.Header.ChunkSize && start + sizeInBlocks > chunk.StartBlock)
            {
                throw new ArgumentException($"块区域 [{start}, {start + sizeInBlocks}) 与现有块 [{chunk.StartBlock}, {chunk.StartBlock + chunk.Header.ChunkSize}) 重叠。");
            }
        }
        return start;
    }

    private void AddChunkSorted(SparseChunk chunk)
    {
        var index = Chunks.FindIndex(c => c.StartBlock > chunk.StartBlock);
        if (index == -1)
        {
            Chunks.Add(chunk);
        }
        else
        {
            Chunks.Insert(index, chunk);
        }
    }

    public delegate int SparseWriteCallback(byte[]? data, int length);

    /// <summary>
    /// 通过回调函数写入 Sparse 文件（对齐 libsparse 的 sparse_file_callback）
    /// </summary>
    public void WriteWithCallback(SparseWriteCallback callback, bool sparse = true, bool includeCrc = false)
    {
        if (!sparse)
        {
            var buffer = new byte[1024 * 1024];
            foreach (var chunk in Chunks)
            {
                var size = (long)chunk.Header.ChunkSize * Header.BlockSize;
                switch (chunk.Header.ChunkType)
                {
                    case SparseFormat.CHUNK_TYPE_RAW:
                        if (chunk.DataProvider != null)
                        {
                            long written = 0;
                            while (written < chunk.DataProvider.Length)
                            {
                                var toRead = (int)Math.Min(buffer.Length, chunk.DataProvider.Length - written);
                                var read = chunk.DataProvider.Read(written, buffer, 0, toRead);
                                if (read <= 0)
                                {
                                    break;
                                }

                                if (callback(buffer, read) < 0)
                                {
                                    return;
                                }

                                written += read;
                            }
                            if (written < size)
                            {
                                Array.Clear(buffer, 0, (int)Math.Min(buffer.Length, size - written));
                                while (written < size)
                                {
                                    var toWrite = (int)Math.Min(buffer.Length, size - written);
                                    if (callback(buffer, toWrite) < 0)
                                    {
                                        return;
                                    }

                                    written += toWrite;
                                }
                            }
                        }
                        else
                        {
                            if (callback(null, (int)size) < 0)
                            {
                                return;
                            }
                        }
                        break;
                    case SparseFormat.CHUNK_TYPE_FILL:
                        var fillValBytes = new byte[4];
                        BinaryPrimitives.WriteUInt32LittleEndian(fillValBytes, chunk.FillValue);
                        for (var i = 0; i <= buffer.Length - 4; i += 4)
                        {
                            Array.Copy(fillValBytes, 0, buffer, i, 4);
                        }

                        var fillRemaining = size;
                        while (fillRemaining > 0)
                        {
                            var toWrite = (int)Math.Min(buffer.Length, fillRemaining);
                            if (callback(buffer, toWrite) < 0)
                            {
                                return;
                            }

                            fillRemaining -= toWrite;
                        }
                        break;
                    case SparseFormat.CHUNK_TYPE_DONT_CARE:
                        if (callback(null, (int)size) < 0)
                        {
                            return;
                        }

                        break;
                    default:
                        break;
                }
            }
            return;
        }

        // Sparse 模式下的回调写入
        using var ms = new MemoryStream();
        WriteToStream(ms, true, false, includeCrc);
        var bytes = ms.ToArray();
        callback(bytes, bytes.Length);
    }

    public void AddRawFileChunk(string filePath, long offset, uint size, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (size + blockSize - 1) / blockSize;
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;
        var currentOffset = offset;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, SparseFormat.MAX_CHUNK_DATA_SIZE);
            if (partSize < remaining && partSize % blockSize != 0)
            {
                partSize = partSize / blockSize * blockSize;
                if (partSize == 0)
                {
                    partSize = remaining;
                }
            }

            var chunkBlocks = (partSize + blockSize - 1) / blockSize;
            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.CHUNK_TYPE_RAW,
                Reserved = 0,
                ChunkSize = chunkBlocks,
                TotalSize = (uint)(SparseFormat.CHUNK_HEADER_SIZE + ((long)chunkBlocks * blockSize))
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                DataProvider = new FileDataProvider(filePath, currentOffset, partSize)
            };

            AddChunkSorted(chunk);
            currentBlockStart += chunkBlocks;
            remaining -= partSize;
            currentOffset += partSize;
        }
    }

    public void AddRawChunk(byte[] data, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (uint)((data.Length + blockSize - 1) / blockSize);
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = (uint)data.Length;
        var currentOffset = 0;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, SparseFormat.MAX_CHUNK_DATA_SIZE);
            if (partSize < remaining && partSize % blockSize != 0)
            {
                partSize = partSize / blockSize * blockSize;
                if (partSize == 0)
                {
                    partSize = remaining;
                }
            }

            var chunkBlocks = (partSize + blockSize - 1) / blockSize;
            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.CHUNK_TYPE_RAW,
                Reserved = 0,
                ChunkSize = chunkBlocks,
                TotalSize = (uint)(SparseFormat.CHUNK_HEADER_SIZE + ((long)chunkBlocks * blockSize))
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                DataProvider = new MemoryDataProvider(data, currentOffset, (int)partSize)
            };

            AddChunkSorted(chunk);
            currentBlockStart += chunkBlocks;
            remaining -= partSize;
            currentOffset += (int)partSize;
        }
    }

    public void AddStreamChunk(Stream stream, long offset, uint size, uint? blockIndex = null, bool leaveOpen = true)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (size + blockSize - 1) / blockSize;
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;
        var currentOffset = offset;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, SparseFormat.MAX_CHUNK_DATA_SIZE);
            if (partSize < remaining && partSize % blockSize != 0)
            {
                partSize = partSize / blockSize * blockSize;
                if (partSize == 0)
                {
                    partSize = remaining;
                }
            }

            var chunkBlocks = (partSize + blockSize - 1) / blockSize;
            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.CHUNK_TYPE_RAW,
                Reserved = 0,
                ChunkSize = chunkBlocks,
                TotalSize = (uint)(SparseFormat.CHUNK_HEADER_SIZE + ((long)chunkBlocks * blockSize))
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                DataProvider = new StreamDataProvider(stream, currentOffset, partSize, leaveOpen)
            };

            AddChunkSorted(chunk);
            currentBlockStart += chunkBlocks;
            remaining -= partSize;
            currentOffset += partSize;
        }
    }

    public void AddFillChunk(uint fillValue, long size, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (uint)((size + blockSize - 1) / blockSize);
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;

        while (remaining > 0)
        {
            var partSize = Math.Min(remaining, (long)0x00FFFFFF * blockSize);

            var partBlocks = (uint)((partSize + blockSize - 1) / blockSize);
            if (partBlocks > 0x00FFFFFF)
            {
                partBlocks = 0x00FFFFFF;
            }

            var actualPartSize = (long)partBlocks * blockSize;
            if (actualPartSize > remaining)
            {
                actualPartSize = remaining;
            }

            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.CHUNK_TYPE_FILL,
                Reserved = 0,
                ChunkSize = partBlocks,
                TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4
            };

            var chunk = new SparseChunk(chunkHeader)
            {
                StartBlock = currentBlockStart,
                FillValue = fillValue
            };

            AddChunkSorted(chunk);
            currentBlockStart += partBlocks;
            remaining -= actualPartSize;
        }
    }

    public void AddDontCareChunk(long size, uint? blockIndex = null)
    {
        var blockSize = Header.BlockSize;
        var totalBlocks = (uint)((size + blockSize - 1) / blockSize);
        var currentBlockStart = GetNextBlockAndCheckOverlap(blockIndex, totalBlocks);

        var remaining = size;

        while (remaining > 0)
        {
            var partBlocks = (uint)((remaining + blockSize - 1) / blockSize);
            if (partBlocks > 0x00FFFFFFu)
            {
                partBlocks = 0x00FFFFFFu;
            }

            var actualPartSize = (long)partBlocks * blockSize;

            var chunkHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.CHUNK_TYPE_DONT_CARE,
                Reserved = 0,
                ChunkSize = partBlocks,
                TotalSize = SparseFormat.CHUNK_HEADER_SIZE
            };

            var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlockStart };
            AddChunkSorted(chunk);
            currentBlockStart += partBlocks;
            remaining -= Math.Min(remaining, actualPartSize);
        }
    }

    public void Dispose()
    {
        foreach (var chunk in Chunks)
        {
            chunk.Dispose();
        }
    }

    public List<SparseFile> Resparse(long maxFileSize)
    {
        var result = new List<SparseFile>();
        long overhead = SparseFormat.SPARSE_HEADER_SIZE + (2 * SparseFormat.CHUNK_HEADER_SIZE) + 4;

        if (maxFileSize <= overhead)
        {
            throw new ArgumentException($"maxFileSize 必须大于基础结构开销 ({overhead} 字节)");
        }

        var fileLimit = maxFileSize - overhead;
        var entries = BuildResparseEntries();

        if (entries.Count == 0)
        {
            var emptyFile = CreateNewSparseForResparse();
            emptyFile.Header = emptyFile.Header with { TotalBlocks = Header.TotalBlocks };
            if (Header.TotalBlocks > 0)
            {
                emptyFile.Chunks.Add(CreateDontCareChunk(Header.TotalBlocks));
            }
            FinishCurrentResparseFile(emptyFile);
            result.Add(emptyFile);
            return result;
        }

        var startIndex = 0;
        while (startIndex < entries.Count)
        {
            long fileLen = 0;
            uint lastBlock = 0;
            var lastIncludedIndex = -1;

            for (var i = startIndex; i < entries.Count; i++)
            {
                var entry = entries[i];
                var count = GetSparseChunkSize(entry.Chunk);
                if (entry.StartBlock > lastBlock)
                {
                    count += SparseFormat.CHUNK_HEADER_SIZE;
                }

                lastBlock = entry.StartBlock + entry.Chunk.Header.ChunkSize;

                if (fileLen + count > fileLimit)
                {
                    fileLen += SparseFormat.CHUNK_HEADER_SIZE;
                    var availableForData = fileLimit - fileLen;
                    var canSplit = lastIncludedIndex < 0 || availableForData > (fileLimit / 8);

                    if (canSplit)
                    {
                        var blocksToTake = availableForData > 0
                            ? (uint)(availableForData / Header.BlockSize)
                            : 0u;

                        if (blocksToTake > 0 && blocksToTake < entry.Chunk.Header.ChunkSize)
                        {
                            var (part1, part2) = SplitChunkInternal(entry.Chunk, blocksToTake);
                            entries[i] = new ResparseEntry(entry.StartBlock, part1);
                            entries.Insert(i + 1, new ResparseEntry(entry.StartBlock + blocksToTake, part2));
                            lastIncludedIndex = i;
                        }
                    }

                    break;
                }

                fileLen += count;
                lastIncludedIndex = i;
            }

            if (lastIncludedIndex < startIndex)
            {
                throw new InvalidOperationException("无法将 Chunk 放入 SparseFile，请增加 maxFileSize。");
            }

            var currentFile = BuildResparseFile(entries, startIndex, lastIncludedIndex);
            result.Add(currentFile);
            startIndex = lastIncludedIndex + 1;
        }

        return result;
    }

    private (SparseChunk First, SparseChunk Second) SplitChunkInternal(SparseChunk chunk, uint blocksToTake)
    {
        var h1 = chunk.Header with { ChunkSize = blocksToTake };
        var h2 = chunk.Header with { ChunkSize = chunk.Header.ChunkSize - blocksToTake };

        if (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_RAW)
        {
            h1.TotalSize = (uint)(SparseFormat.CHUNK_HEADER_SIZE + ((long)blocksToTake * Header.BlockSize));
            h2.TotalSize = (uint)(SparseFormat.CHUNK_HEADER_SIZE + ((long)h2.ChunkSize * Header.BlockSize));
        }
        else if (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_FILL)
        {
            h1.TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4;
            h2.TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4;
        }
        else
        {
            h1.TotalSize = SparseFormat.CHUNK_HEADER_SIZE;
            h2.TotalSize = SparseFormat.CHUNK_HEADER_SIZE;
        }

        var part1 = new SparseChunk(h1);
        var part2 = new SparseChunk(h2);

        if (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_RAW && chunk.DataProvider != null)
        {
            part1.DataProvider = chunk.DataProvider.GetSubProvider(0, (long)blocksToTake * Header.BlockSize);
            part2.DataProvider = chunk.DataProvider.GetSubProvider((long)blocksToTake * Header.BlockSize, (long)h2.ChunkSize * Header.BlockSize);
        }
        else if (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_FILL)
        {
            part1.FillValue = chunk.FillValue;
            part2.FillValue = chunk.FillValue;
        }

        return (part1, part2);
    }

    private SparseFile CreateNewSparseForResparse()
    {
        return new SparseFile
        {
            Header = new SparseHeader
            {
                Magic = SparseFormat.SPARSE_HEADER_MAGIC,
                MajorVersion = Header.MajorVersion,
                MinorVersion = Header.MinorVersion,
                FileHeaderSize = Header.FileHeaderSize,
                ChunkHeaderSize = Header.ChunkHeaderSize,
                BlockSize = Header.BlockSize
            }
        };
    }

    private void FinishCurrentResparseFile(SparseFile file)
    {
        var header = file.Header;
        header.TotalChunks = (uint)file.Chunks.Count;
        header.TotalBlocks = (uint)file.Chunks.Sum(c => c.Header.ChunkSize);
        file.Header = header;
    }

    private sealed class ResparseEntry(uint startBlock, SparseChunk chunk)
    {
        public uint StartBlock { get; } = startBlock;
        public SparseChunk Chunk { get; } = chunk;
    }

    private List<ResparseEntry> BuildResparseEntries()
    {
        var entries = new List<ResparseEntry>();
        uint currentBlock = 0;

        foreach (var chunk in Chunks)
        {
            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                case SparseFormat.CHUNK_TYPE_FILL:
                    entries.Add(new ResparseEntry(currentBlock, chunk));
                    break;
                default:
                    break;
            }

            currentBlock += chunk.Header.ChunkSize;
        }

        return entries;
    }

    private long GetSparseChunkSize(SparseChunk chunk)
    {
        return chunk.Header.ChunkType switch
        {
            SparseFormat.CHUNK_TYPE_RAW => SparseFormat.CHUNK_HEADER_SIZE + ((long)chunk.Header.ChunkSize * Header.BlockSize),
            SparseFormat.CHUNK_TYPE_FILL => SparseFormat.CHUNK_HEADER_SIZE + 4,
            _ => SparseFormat.CHUNK_HEADER_SIZE
        };
    }

    private SparseChunk CreateDontCareChunk(uint blocks)
    {
        return new SparseChunk(new ChunkHeader
        {
            ChunkType = SparseFormat.CHUNK_TYPE_DONT_CARE,
            Reserved = 0,
            ChunkSize = blocks,
            TotalSize = SparseFormat.CHUNK_HEADER_SIZE
        });
    }

    private SparseFile BuildResparseFile(List<ResparseEntry> entries, int startIndex, int endIndex)
    {
        var file = CreateNewSparseForResparse();
        file.Header = file.Header with { TotalBlocks = Header.TotalBlocks };

        uint currentBlock = 0;
        for (var i = startIndex; i <= endIndex; i++)
        {
            var entry = entries[i];
            if (entry.StartBlock > currentBlock)
            {
                file.Chunks.Add(CreateDontCareChunk(entry.StartBlock - currentBlock));
            }

            file.Chunks.Add(entry.Chunk);
            currentBlock = entry.StartBlock + entry.Chunk.Header.ChunkSize;
        }

        if (currentBlock < Header.TotalBlocks)
        {
            file.Chunks.Add(CreateDontCareChunk(Header.TotalBlocks - currentBlock));
        }

        FinishCurrentResparseFile(file);
        return file;
    }

    public Stream GetExportStream(uint startBlock, uint blockCount, bool includeCrc = false) => new SparseImageStream(this, startBlock, blockCount, includeCrc);

    public IEnumerable<Stream> GetResparsedStreams(long maxFileSize, bool includeCrc = false)
    {
        foreach (var file in Resparse(maxFileSize))
        {
            yield return new SparseImageStream(file, 0, file.Header.TotalBlocks, includeCrc, false);
        }
    }

    /// <summary>
    /// 遍历所有包含数据的 Chunk (RAW/FILL)（对齐 libsparse 的 sparse_file_foreach_chunk）
    /// </summary>
    public void ForeachChunk(Action<SparseChunk, uint, uint> action)
    {
        uint currentBlock = 0;
        foreach (var chunk in Chunks)
        {
            if (chunk.Header.ChunkType is SparseFormat.CHUNK_TYPE_RAW or
                SparseFormat.CHUNK_TYPE_FILL)
            {
                action(chunk, currentBlock, chunk.Header.ChunkSize);
            }
            currentBlock += chunk.Header.ChunkSize;
        }
    }

    /// <summary>
    /// 遍历所有 Chunk，包括 DONT_CARE
    /// </summary>
    public void ForeachChunkAll(Action<SparseChunk, uint, uint> action)
    {
        uint currentBlock = 0;
        foreach (var chunk in Chunks)
        {
            action(chunk, currentBlock, chunk.Header.ChunkSize);
            currentBlock += chunk.Header.ChunkSize;
        }
    }

    /// <summary>
    /// 获取如果写入磁盘时的长度（对齐 libsparse 的 sparse_file_len）
    /// </summary>
    public long GetLength(bool sparse, bool includeCrc)
    {
        if (!sparse)
        {
            return (long)Header.TotalBlocks * Header.BlockSize;
        }

        long length = SparseFormat.SPARSE_HEADER_SIZE;
        foreach (var chunk in Chunks)
        {
            length += chunk.Header.TotalSize;
        }

        var sumBlocks = (uint)Chunks.Sum(c => c.Header.ChunkSize);
        if (Header.TotalBlocks > sumBlocks)
        {
            length += SparseFormat.CHUNK_HEADER_SIZE;
        }

        if (includeCrc)
        {
            length += SparseFormat.CHUNK_HEADER_SIZE + 4;
        }

        return length;
    }

    /// <summary>
    /// 智能导入：自动判断是 Sparse 还是 Raw 镜像（对齐 libsparse 的 sparse_file_import_auto）
    /// </summary>
    public static SparseFile ImportAuto(string filePath, bool validateCrc = false, bool verbose = false)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return ImportAuto(stream, validateCrc, verbose, filePath);
    }

    /// <summary>
    /// 智能导入：通过流自动判断（对齐 libsparse 的 sparse_file_import_auto）
    /// </summary>
    public static SparseFile ImportAuto(Stream stream, bool validateCrc = false, bool verbose = false, string? filePath = null)
    {
        var magicData = new byte[4];
        var pos = stream.CanSeek ? stream.Position : 0;
        if (stream.Read(magicData, 0, 4) == 4)
        {
            var magic = BinaryPrimitives.ReadUInt32LittleEndian(magicData);
            if (stream.CanSeek)
            {
                stream.Seek(pos, SeekOrigin.Begin);
            }

            if (magic == SparseFormat.SPARSE_HEADER_MAGIC)
            {
                return FromStreamInternal(stream, filePath, validateCrc, verbose);
            }
        }

        if (filePath != null)
        {
            return FromRawFile(filePath, 4096, verbose);
        }

        // 如果没有提供路径且不是 Sparse，则作为 Raw 流处理
        var rawFile = new SparseFile(4096, stream.Length, verbose);
        rawFile.ReadFromStream(stream, SparseReadMode.Normal);
        return rawFile;
    }

    /// <summary>
    /// 从原始文件读取并稀疏化（对齐 libsparse 的 sparse_file_read）
    /// </summary>
    public static SparseFile FromRawFile(string filePath, uint blockSize = 4096, bool verbose = false)
    {
        var fi = new FileInfo(filePath);
        var sparseFile = new SparseFile(blockSize, fi.Length, verbose);
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read);
        sparseFile.ReadFromStream(fs, SparseReadMode.Normal);
        return sparseFile;
    }

    /// <summary>
    /// 从流中读取并稀疏化或解析 Sparse 格式（对齐 libsparse 的 sparse_file_read）
    /// </summary>
    public void ReadFromStream(Stream stream, SparseReadMode mode, bool validateCrc = false)
    {
        if (mode == SparseReadMode.Sparse)
        {
            var headerData = new byte[SparseFormat.SPARSE_HEADER_SIZE];
            if (stream.Read(headerData, 0, headerData.Length) != headerData.Length)
            {
                throw new InvalidDataException("无法读取 sparse 文件头");
            }

            var importedHeader = SparseHeader.FromBytes(headerData);
            if (!importedHeader.IsValid())
            {
                throw new InvalidDataException("无效的 sparse 文件头");
            }

            if (Header.BlockSize != importedHeader.BlockSize)
            {
                throw new ArgumentException("导入的 Sparse 文件块大小与当前文件不匹配");
            }

            if (Verbose)
            {
                SparseLogger.Info($"ReadFromStream (Sparse模式): BlockSize={importedHeader.BlockSize}, TotalBlocks={importedHeader.TotalBlocks}, TotalChunks={importedHeader.TotalChunks}");
            }

            stream.Seek(importedHeader.FileHeaderSize - SparseFormat.SPARSE_HEADER_SIZE, SeekOrigin.Current);

            var checksum = Crc32.Begin();
            var currentBlockStart = CurrentBlock;

            for (uint i = 0; i < importedHeader.TotalChunks; i++)
            {
                var chunkHeaderData = new byte[SparseFormat.CHUNK_HEADER_SIZE];
                stream.ReadExactly(chunkHeaderData, 0, chunkHeaderData.Length);
                var chunkHeader = ChunkHeader.FromBytes(chunkHeaderData);

                if (Verbose)
                {
                    SparseLogger.Info($"导入 Chunk #{i}: 类型=0x{chunkHeader.ChunkType:X4}, 大小={chunkHeader.ChunkSize} blocks");
                }

                stream.Seek(importedHeader.ChunkHeaderSize - SparseFormat.CHUNK_HEADER_SIZE, SeekOrigin.Current);

                var dataSize = (long)chunkHeader.TotalSize - importedHeader.ChunkHeaderSize;
                var expectedRawSize = (long)chunkHeader.ChunkSize * Header.BlockSize;

                var chunk = new SparseChunk(chunkHeader) { StartBlock = currentBlockStart };

                switch (chunkHeader.ChunkType)
                {
                    case SparseFormat.CHUNK_TYPE_RAW:
                        var rawData = new byte[dataSize];
                        stream.ReadExactly(rawData, 0, (int)dataSize);
                        if (validateCrc)
                        {
                            checksum = Crc32.Update(checksum, rawData);
                        }
                        chunk.DataProvider = new MemoryDataProvider(rawData);
                        Chunks.Add(chunk);
                        currentBlockStart += chunkHeader.ChunkSize;
                        break;
                    case SparseFormat.CHUNK_TYPE_FILL:
                        var fillData = new byte[4];
                        stream.ReadExactly(fillData, 0, 4);
                        var fillValue = BinaryPrimitives.ReadUInt32LittleEndian(fillData);
                        if (validateCrc)
                        {
                            // 简化 CRC 计算
                            for (var j = 0; j < expectedRawSize / 4; j++)
                            {
                                checksum = Crc32.Update(checksum, fillData);
                            }
                        }
                        chunk.FillValue = fillValue;
                        Chunks.Add(chunk);
                        currentBlockStart += chunkHeader.ChunkSize;
                        if (dataSize > 4)
                        {
                            stream.Seek(dataSize - 4, SeekOrigin.Current);
                        }
                        break;
                    case SparseFormat.CHUNK_TYPE_DONT_CARE:
                        if (validateCrc)
                        {
                            var zero4 = new byte[4];
                            for (var j = 0; j < expectedRawSize / 4; j++)
                            {
                                checksum = Crc32.Update(checksum, zero4);
                            }
                        }
                        Chunks.Add(chunk);
                        currentBlockStart += chunkHeader.ChunkSize;
                        break;
                    case SparseFormat.CHUNK_TYPE_CRC32:
                        var crcFileData = new byte[4];
                        stream.ReadExactly(crcFileData, 0, 4);
                        if (validateCrc)
                        {
                            var fileCrc = BinaryPrimitives.ReadUInt32LittleEndian(crcFileData);
                            if (fileCrc != Crc32.Finish(checksum))
                            {
                                throw new InvalidDataException("CRC32 校验失败");
                            }
                        }
                        break;
                    default:
                        break;
                }
            }
            return;
        }

        // Normal 或 Hole 模式：扫描流并进行稀疏化
        var blockSize = Header.BlockSize;
        var bufferScan = new byte[blockSize];
        long currentPos = 0;
        var streamLen = stream.Length;
        long rawStart = -1;

        while (currentPos < streamLen)
        {
            if (stream.CanSeek)
            {
                stream.Position = currentPos;
            }
            var bytesRead = stream.Read(bufferScan, 0, (int)Math.Min(blockSize, streamLen - currentPos));
            if (bytesRead == 0)
            {
                break;
            }

            uint fillValue = 0;
            var isZero = IsZeroBlock(bufferScan, bytesRead);
            var isFill = !isZero && bytesRead == blockSize && IsFillBlock(bufferScan, out fillValue);

            if (isZero || isFill)
            {
                if (rawStart != -1)
                {
                    AddStreamChunk(stream, rawStart, (uint)(currentPos - rawStart));
                    rawStart = -1;
                }

                if (isZero)
                {
                    var zeroStart = currentPos;
                    currentPos += bytesRead;
                    while (currentPos < streamLen)
                    {
                        var innerRead = stream.Read(bufferScan, 0, (int)Math.Min(blockSize, streamLen - currentPos));
                        if (innerRead > 0 && IsZeroBlock(bufferScan, innerRead))
                        {
                            currentPos += innerRead;
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (mode == SparseReadMode.Hole)
                    {
                        AddDontCareChunk(currentPos - zeroStart);
                    }
                    else
                    {
                        AddFillChunk(0, currentPos - zeroStart);
                    }
                }
                else
                {
                    var fillStart = currentPos;
                    var currentFillValue = fillValue;
                    currentPos += bytesRead;
                    while (currentPos < streamLen)
                    {
                        var innerRead = stream.Read(bufferScan, 0, (int)Math.Min(blockSize, streamLen - currentPos));
                        if (innerRead == blockSize && IsFillBlock(bufferScan, out var innerFill) && innerFill == currentFillValue)
                        {
                            currentPos += innerRead;
                        }
                        else
                        {
                            break;
                        }
                    }
                    AddFillChunk(currentFillValue, currentPos - fillStart);
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
            AddStreamChunk(stream, rawStart, (uint)(streamLen - rawStart));
        }
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

        var pattern = BinaryPrimitives.ReadUInt32LittleEndian(buffer);
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