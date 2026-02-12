namespace LibSparseSharp;

/// <summary>
/// 数据提供者接口，用于抽象内存数据和文件数据
/// </summary>
public interface ISparseDataProvider : IDisposable
{
    long Length { get; }
    void WriteTo(Stream stream);
    int Read(long offset, byte[] buffer, int bufferOffset, int count);
}

public class MemoryDataProvider(byte[] data) : ISparseDataProvider
{
    public long Length => data.Length;
    public void WriteTo(Stream stream) => stream.Write(data, 0, data.Length);
    public int Read(long offset, byte[] buffer, int bufferOffset, int count)
    {
        var available = (int)Math.Max(0, data.Length - offset);
        var toCopy = Math.Min(count, available);
        if (toCopy <= 0)
        {
            return 0;
        }

        Array.Copy(data, (int)offset, buffer, bufferOffset, toCopy);
        return toCopy;
    }
    public void Dispose() { }
}

public class FileDataProvider(string filePath, long offset, long length) : ISparseDataProvider
{
    public long Length => length;
    public void WriteTo(Stream stream)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        fs.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[1024 * 1024]; // 1MB 缓冲区
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
    public void Dispose() { }
}

/// <summary>
/// Sparse块数据结构
/// </summary>
public class SparseChunk(ChunkHeader header) : IDisposable
{
    public ChunkHeader Header { get; set; } = header;
    public ISparseDataProvider? DataProvider { get; set; }
    public uint FillValue { get; set; }

    public void Dispose() => DataProvider?.Dispose();
}

/// <summary>
/// Sparse文件结构
/// </summary>
public class SparseFile : IDisposable
{
    /// <summary>
    /// 只读取sparse文件头部信息（不解析 chunk）
    /// </summary>
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

    public SparseFile() { }

    public SparseFile(uint blockSize, long totalSize)
    {
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

    /// <summary>
    /// 从文件流读取sparse文件
    /// </summary>
    public static SparseFile FromStream(Stream stream) => FromStreamInternal(stream, null);

    /// <summary>
    /// 从文件路径读取sparse文件（推荐，支持大型文件的按需读取）
    /// </summary>
    public static SparseFile FromImageFile(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return FromStreamInternal(stream, filePath);
    }

    private static SparseFile FromStreamInternal(Stream stream, string? filePath)
    {
        SparseLogger.Info("正在解析 Sparse 文件头...");
        var sparseFile = new SparseFile();
        var headerData = new byte[SparseFormat.SPARSE_HEADER_SIZE];
        if (stream.Read(headerData, 0, headerData.Length) != headerData.Length)
        {
            throw new InvalidDataException("无法读取 sparse 文件头");
        }

        sparseFile.Header = SparseHeader.FromBytes(headerData);

        if (!sparseFile.Header.IsValid())
        {
            throw new InvalidDataException("无效的 sparse 文件头");
        }

        SparseLogger.Info($"Sparse 文件解析: 版本 {sparseFile.Header.MajorVersion}.{sparseFile.Header.MinorVersion}, " +
                          $"Chunk 总数: {sparseFile.Header.TotalChunks}, 总块数: {sparseFile.Header.TotalBlocks}");

        // 如果文件头比标准大小大，跳过额外部分
        if (sparseFile.Header.FileHeaderSize > SparseFormat.SPARSE_HEADER_SIZE)
        {
            stream.Seek(sparseFile.Header.FileHeaderSize - SparseFormat.SPARSE_HEADER_SIZE, SeekOrigin.Current);
        }

        for (uint i = 0; i < sparseFile.Header.TotalChunks; i++)
        {
            if (i % 500 == 0 && i > 0) SparseLogger.Info($"正在加载第 {i}/{sparseFile.Header.TotalChunks} 个 Chunk...");
            var chunkHeaderData = new byte[SparseFormat.CHUNK_HEADER_SIZE];
            if (stream.Read(chunkHeaderData, 0, chunkHeaderData.Length) != chunkHeaderData.Length)
            {
                throw new InvalidDataException($"无法读取第 {i} 个 chunk 头");
            }

            var chunkHeader = ChunkHeader.FromBytes(chunkHeaderData);

            // 如果 Chunk 头比标准大小大，跳过额外部分
            if (sparseFile.Header.ChunkHeaderSize > SparseFormat.CHUNK_HEADER_SIZE)
            {
                stream.Seek(sparseFile.Header.ChunkHeaderSize - SparseFormat.CHUNK_HEADER_SIZE, SeekOrigin.Current);
            }

            var chunk = new SparseChunk(chunkHeader);

            if (!chunkHeader.IsValid())
            {
                throw new InvalidDataException($"第 {i} 个 chunk 头无效");
            }

            var dataSize = (long)chunkHeader.TotalSize - SparseFormat.CHUNK_HEADER_SIZE;

            switch (chunkHeader.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (filePath != null)
                    {
                        // 使用 FileDataProvider 进行按需读取，不占用内存
                        chunk.DataProvider = new FileDataProvider(filePath, stream.Position, dataSize);
                        stream.Seek(dataSize, SeekOrigin.Current);
                    }
                    else
                    {
                        // 降级为内存读取
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
                    if (dataSize >= 4)
                    {
                        var fillData = new byte[4];
                        if (stream.Read(fillData, 0, 4) != 4)
                        {
                            throw new InvalidDataException($"无法读取第 {i} 个 chunk 的填充值");
                        }

                        chunk.FillValue = BitConverter.ToUInt32(fillData, 0);

                        if (dataSize > 4)
                        {
                            stream.Seek(dataSize - 4, SeekOrigin.Current);
                        }
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                    if (dataSize > 0)
                    {
                        stream.Seek(dataSize, SeekOrigin.Current);
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_CRC32:
                    if (dataSize > 0)
                    {
                        stream.Seek(dataSize, SeekOrigin.Current);
                    }
                    break;

                default:
                    throw new InvalidDataException($"第 {i} 个 chunk 类型未知: 0x{chunkHeader.ChunkType:X4}");
            }

            sparseFile.Chunks.Add(chunk);
        }

        return sparseFile;
    }

    /// <summary>
    /// 将sparse文件写入流
    /// </summary>
    public void WriteToStream(Stream stream, bool includeCrc = false)
    {
        var header = Header;
        header.TotalChunks = (uint)Chunks.Count;
        if (includeCrc)
        {
            header.TotalChunks++; // 为 CRC chunk 预留空间
        }
        Header = header;

        var headerData = Header.ToBytes();
        stream.Write(headerData, 0, headerData.Length);

        var checksum = Crc32.Begin();

        foreach (var chunk in Chunks)
        {
            var chunkHeaderData = chunk.Header.ToBytes();
            stream.Write(chunkHeaderData, 0, chunkHeaderData.Length);

            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (chunk.DataProvider != null)
                    {
                        if (includeCrc)
                        {
                            using var ms = new MemoryStream();
                            chunk.DataProvider.WriteTo(ms);
                            var data = ms.ToArray();
                            checksum = Crc32.Update(checksum, data);
                            stream.Write(data, 0, data.Length);
                        }
                        else
                        {
                            chunk.DataProvider.WriteTo(stream);
                        }
                    }
                    else
                    {
                        // 如果缺少提供者但头部指示为 RAW，则填充 0
                        var zeros = new byte[chunk.Header.ChunkSize * Header.BlockSize];
                        if (includeCrc)
                        {
                            checksum = Crc32.Update(checksum, zeros);
                        }

                        stream.Write(zeros, 0, zeros.Length);
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_FILL:
                    var fillData = BitConverter.GetBytes(chunk.FillValue);
                    stream.Write(fillData, 0, fillData.Length);
                    if (includeCrc)
                    {
                        // 对于 CRC 校验，Fill chunk 被视为多次重复的 4 字节值
                        var fillVal = chunk.FillValue;
                        var valBytes = BitConverter.GetBytes(fillVal);
                        var totalBytes = (long)chunk.Header.ChunkSize * Header.BlockSize;
                        for (long i = 0; i < totalBytes; i += 4)
                        {
                            checksum = Crc32.Update(checksum, valBytes);
                        }
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                    if (includeCrc)
                    {
                        // 在 libsparse 中，DontCare chunk 在计算 CRC 时被视为 0
                        var zeros = new byte[chunk.Header.ChunkSize * Header.BlockSize];
                        checksum = Crc32.Update(checksum, zeros);
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_CRC32:
                    break;
                default:
                    break;
            }
        }

        if (includeCrc)
        {
            checksum = Crc32.Finish(checksum);
            var crcHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.CHUNK_TYPE_CRC32,
                Reserved = 0,
                ChunkSize = 0,
                TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4
            };
            stream.Write(crcHeader.ToBytes(), 0, SparseFormat.CHUNK_HEADER_SIZE);
            stream.Write(BitConverter.GetBytes(checksum), 0, 4);

            // 更新流中的头部信息（回到开头）
            var finalHeader = Header;
            finalHeader.ImageChecksum = checksum;
            Header = finalHeader;
            var currentPos = stream.Position;
            stream.Seek(0, SeekOrigin.Begin);
            stream.Write(Header.ToBytes(), 0, SparseFormat.SPARSE_HEADER_SIZE);
            stream.Seek(currentPos, SeekOrigin.Begin);
        }
    }

    /// <summary>
    /// 将 SparseFile 写入为 Raw 镜像（解包/导出）
    /// </summary>
    public void WriteRawToStream(Stream stream)
    {
        foreach (var chunk in Chunks)
        {
            var size = (long)chunk.Header.ChunkSize * Header.BlockSize;
            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (chunk.DataProvider != null)
                    {
                        chunk.DataProvider.WriteTo(stream);
                    }
                    else
                    {
                        // 预留空间但不写入（如果是稀疏流则会产生空洞）
                        stream.Seek(size, SeekOrigin.Current);
                    }
                    break;
                case SparseFormat.CHUNK_TYPE_FILL:
                    var fillVal = BitConverter.GetBytes(chunk.FillValue);
                    var fillBuf = new byte[Header.BlockSize];
                    for (var i = 0; i < Header.BlockSize; i += 4)
                    {
                        Array.Copy(fillVal, 0, fillBuf, i, 4);
                    }

                    for (var i = 0; i < chunk.Header.ChunkSize; i++)
                    {
                        stream.Write(fillBuf, 0, fillBuf.Length);
                    }

                    break;
                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                    stream.Seek(size, SeekOrigin.Current);
                    break;
                default:
                    break;
            }
        }

        // 确保文件长度达到预期
        var expectedLength = (long)Header.TotalBlocks * Header.BlockSize;
        if (stream.Length < expectedLength)
        {
            stream.SetLength(expectedLength);
        }
    }

    /// <summary>
    /// 从文件路径添加原始数据块（Backed Block）
    /// </summary>
    public void AddRawFileChunk(string filePath, long offset, uint size)
    {
        var blockSize = Header.BlockSize;
        var chunkBlocks = (size + blockSize - 1) / blockSize;

        var chunkHeader = new ChunkHeader
        {
            ChunkType = SparseFormat.CHUNK_TYPE_RAW,
            Reserved = 0,
            ChunkSize = chunkBlocks,
            TotalSize = SparseFormat.CHUNK_HEADER_SIZE + size
        };

        var chunk = new SparseChunk(chunkHeader)
        {
            DataProvider = new FileDataProvider(filePath, offset, size)
        };

        Chunks.Add(chunk);
    }

    /// <summary>
    /// 添加内存原始数据块
    /// </summary>
    public void AddRawChunk(byte[] data)
    {
        var dataSize = (uint)data.Length;
        var blockSize = Header.BlockSize;
        var chunkBlocks = (dataSize + blockSize - 1) / blockSize;

        var chunkHeader = new ChunkHeader
        {
            ChunkType = SparseFormat.CHUNK_TYPE_RAW,
            Reserved = 0,
            ChunkSize = chunkBlocks,
            TotalSize = SparseFormat.CHUNK_HEADER_SIZE + dataSize
        };

        var chunk = new SparseChunk(chunkHeader)
        {
            DataProvider = new MemoryDataProvider(data)
        };

        Chunks.Add(chunk);
    }

    /// <summary>
    /// 添加填充块
    /// </summary>
    public void AddFillChunk(uint fillValue, uint size)
    {
        var blockSize = Header.BlockSize;
        var chunkBlocks = (size + blockSize - 1) / blockSize;

        var chunkHeader = new ChunkHeader
        {
            ChunkType = SparseFormat.CHUNK_TYPE_FILL,
            Reserved = 0,
            ChunkSize = chunkBlocks,
            TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4
        };

        var chunk = new SparseChunk(chunkHeader)
        {
            FillValue = fillValue
        };

        Chunks.Add(chunk);
    }

    /// <summary>
    /// 添加空数据块
    /// </summary>
    public void AddDontCareChunk(uint size)
    {
        var blockSize = Header.BlockSize;
        var chunkBlocks = (size + blockSize - 1) / blockSize;

        var chunkHeader = new ChunkHeader
        {
            ChunkType = SparseFormat.CHUNK_TYPE_DONT_CARE,
            Reserved = 0,
            ChunkSize = chunkBlocks,
            TotalSize = SparseFormat.CHUNK_HEADER_SIZE
        };

        var chunk = new SparseChunk(chunkHeader);
        Chunks.Add(chunk);
    }

    public void Dispose()
    {
        foreach (var chunk in Chunks)
        {
            chunk.Dispose();
        }
    }

    /// <summary>
    /// 将大的 SparseFile 拆分为多个不超过指定大小的 SparseFile (Resparse)
    /// </summary>
    public List<SparseFile> Resparse(long maxFileSize)
    {
        var result = new List<SparseFile>();
        if (maxFileSize <= SparseFormat.SPARSE_HEADER_SIZE)
        {
            throw new ArgumentException("maxFileSize 必须大于文件头大小");
        }

        var currentFile = CreateNewSparseForResparse();
        long currentSize = SparseFormat.SPARSE_HEADER_SIZE;

        foreach (var chunk in Chunks)
        {
            long chunkSizeInFile = chunk.Header.TotalSize;

            // 如果单个 chunk 超过了限制，且当前文件已有内容，先保存当前文件
            if (currentSize + chunkSizeInFile > maxFileSize && currentFile.Chunks.Count > 0)
            {
                FinishCurrentResparseFile(currentFile);
                result.Add(currentFile);
                currentFile = CreateNewSparseForResparse();
                currentSize = SparseFormat.SPARSE_HEADER_SIZE;
            }

            // 如果单个 chunk 依然超过限制（即一个 RAW 块就很大），需要拆分该 chunk (仅对 RAW 和 DONT_CARE 有效)
            if (chunkSizeInFile > (maxFileSize - SparseFormat.SPARSE_HEADER_SIZE))
            {
                SplitAndAddChunk(currentFile, chunk, maxFileSize, ref result, ref currentSize);
                currentFile = result[^1]; // Get the last created file
                result.RemoveAt(result.Count - 1); // We will add it back in the next iteration or at the end
            }
            else
            {
                currentFile.Chunks.Add(chunk);
                currentSize += chunkSizeInFile;
            }
        }

        if (currentFile.Chunks.Count > 0)
        {
            FinishCurrentResparseFile(currentFile);
            result.Add(currentFile);
        }

        return result;
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
        file.Header = new SparseHeader
        {
            Magic = file.Header.Magic,
            MajorVersion = file.Header.MajorVersion,
            MinorVersion = file.Header.MinorVersion,
            FileHeaderSize = file.Header.FileHeaderSize,
            ChunkHeaderSize = file.Header.ChunkHeaderSize,
            BlockSize = file.Header.BlockSize,
            TotalChunks = (uint)file.Chunks.Count,
            TotalBlocks = (uint)file.Chunks.Sum(c => c.Header.ChunkSize)
        };
    }

    private void SplitAndAddChunk(SparseFile currentFile, SparseChunk chunk, long maxFileSize, ref List<SparseFile> results, ref long currentSize)
    {
        if (chunk.Header.ChunkType is not SparseFormat.CHUNK_TYPE_RAW and not SparseFormat.CHUNK_TYPE_DONT_CARE)
        {
            // FILL 块很小，不需要拆分，直接放入新文件
            FinishCurrentResparseFile(currentFile);
            results.Add(currentFile);
            var next = CreateNewSparseForResparse();
            next.Chunks.Add(chunk);
            currentSize = SparseFormat.SPARSE_HEADER_SIZE + chunk.Header.TotalSize;
            results.Add(next);
            return;
        }

        var blocksPerFile = (uint)((maxFileSize - SparseFormat.SPARSE_HEADER_SIZE - SparseFormat.CHUNK_HEADER_SIZE) / Header.BlockSize);
        var remainingBlocks = chunk.Header.ChunkSize;
        uint chunkStartBlock = 0;

        while (remainingBlocks > 0)
        {
            var toTake = Math.Min(remainingBlocks, GetBlocksNeededToFill(currentFile, maxFileSize));
            if (toTake == 0)
            {
                FinishCurrentResparseFile(currentFile);
                results.Add(currentFile);
                currentFile = CreateNewSparseForResparse();
                toTake = Math.Min(remainingBlocks, blocksPerFile);
            }

            var subHeader = chunk.Header;
            subHeader.ChunkSize = toTake;
            subHeader.TotalSize = (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_RAW)
                ? SparseFormat.CHUNK_HEADER_SIZE + (toTake * Header.BlockSize)
                : SparseFormat.CHUNK_HEADER_SIZE;

            var subChunk = new SparseChunk(subHeader);
            if (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_RAW && chunk.DataProvider != null)
            {
                // 注意：FileDataProvider 通过偏移量原生支持子范围
                if (chunk.DataProvider is FileDataProvider)
                {
                    // 这里由于需要原始文件路径，逻辑尚未完整实现
                    // 暂且假设在不拷贝的情况下无法轻易拆分 MemoryDataProvider
                    // 目前仅作逻辑演示
                }
            }

            currentFile.Chunks.Add(subChunk);
            remainingBlocks -= toTake;
            chunkStartBlock += toTake;
        }
        results.Add(currentFile);
    }

    private uint GetBlocksNeededToFill(SparseFile file, long maxFileSize)
    {
        var currentSize = SparseFormat.SPARSE_HEADER_SIZE + file.Chunks.Sum(c => c.Header.TotalSize);
        var available = maxFileSize - currentSize - SparseFormat.CHUNK_HEADER_SIZE;
        return available <= 0 ? 0 : (uint)(available / Header.BlockSize);
    }
}