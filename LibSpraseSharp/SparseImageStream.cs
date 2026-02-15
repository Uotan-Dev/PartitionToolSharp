
namespace LibSparseSharp;

/// <summary>
/// 映射区块为完整稀疏镜像流
/// </summary>
public class SparseImageStream : Stream
{
    private readonly uint _blockSize;
    private readonly List<SparseChunk> _mappedChunks = [];
    private readonly List<Section> _sections = [];
    private readonly long _totalByteLength;
    private long _position;

    private struct Section
    {
        public long StartByteOffset;
        public long Length;
        public SectionType Type;
        public int ChunkIndex; // 仅对 ChunkHeader 和 ChunkData 有效
        public byte[]? StaticData; // 用于 Header 缓存
    }

    private enum SectionType
    {
        SparseHeader,
        ChunkHeader,
        ChunkData,
        CrcHeader,
        CrcData
    }

    /// <summary>
    /// 构造映射流
    /// </summary>
    public SparseImageStream(SparseFile source, uint startBlock, uint blockCount, bool includeCrc = false)
    {
        _blockSize = source.Header.BlockSize;

        MapChunks(source, startBlock, blockCount);

        long currentByteOffset = 0;
        var totalChunks = (uint)_mappedChunks.Count;
        uint imageChecksum = 0;

        if (includeCrc)
        {
            totalChunks++;
            imageChecksum = CalculateChecksum();
        }

        var header = new SparseHeader
        {
            Magic = SparseFormat.SPARSE_HEADER_MAGIC,
            MajorVersion = 1,
            MinorVersion = 0,
            FileHeaderSize = SparseFormat.SPARSE_HEADER_SIZE,
            ChunkHeaderSize = SparseFormat.CHUNK_HEADER_SIZE,
            BlockSize = _blockSize,
            TotalBlocks = blockCount,
            TotalChunks = totalChunks,
            ImageChecksum = imageChecksum
        };
        var headerBytes = header.ToBytes();
        _sections.Add(new Section
        {
            StartByteOffset = 0,
            Length = headerBytes.Length,
            Type = SectionType.SparseHeader,
            StaticData = headerBytes
        });
        currentByteOffset += headerBytes.Length;

        for (var i = 0; i < _mappedChunks.Count; i++)
        {
            var chunk = _mappedChunks[i];
            var chunkHeaderBytes = chunk.Header.ToBytes();

            _sections.Add(new Section
            {
                StartByteOffset = currentByteOffset,
                Length = SparseFormat.CHUNK_HEADER_SIZE,
                Type = SectionType.ChunkHeader,
                ChunkIndex = i,
                StaticData = chunkHeaderBytes
            });
            currentByteOffset += SparseFormat.CHUNK_HEADER_SIZE;

            var dataSize = (long)chunk.Header.TotalSize - SparseFormat.CHUNK_HEADER_SIZE;
            if (dataSize > 0)
            {
                _sections.Add(new Section
                {
                    StartByteOffset = currentByteOffset,
                    Length = dataSize,
                    Type = SectionType.ChunkData,
                    ChunkIndex = i
                });
                currentByteOffset += dataSize;
            }
        }

        if (includeCrc)
        {
            var crcHeader = new ChunkHeader
            {
                ChunkType = SparseFormat.CHUNK_TYPE_CRC32,
                Reserved = 0,
                ChunkSize = 0,
                TotalSize = SparseFormat.CHUNK_HEADER_SIZE + 4
            };
            var crcHeaderBytes = crcHeader.ToBytes();
            _sections.Add(new Section
            {
                StartByteOffset = currentByteOffset,
                Length = crcHeaderBytes.Length,
                Type = SectionType.CrcHeader,
                StaticData = crcHeaderBytes
            });
            currentByteOffset += crcHeaderBytes.Length;

            var crcBytes = BitConverter.GetBytes(imageChecksum);
            _sections.Add(new Section
            {
                StartByteOffset = currentByteOffset,
                Length = crcBytes.Length,
                Type = SectionType.CrcData,
                StaticData = crcBytes
            });
            currentByteOffset += crcBytes.Length;
        }

        _totalByteLength = currentByteOffset;
    }

    private uint CalculateChecksum()
    {
        var checksum = Crc32.Begin();
        var buffer = new byte[1024 * 1024];

        foreach (var chunk in _mappedChunks)
        {
            var totalBytes = (long)chunk.Header.ChunkSize * _blockSize;
            switch (chunk.Header.ChunkType)
            {
                case SparseFormat.CHUNK_TYPE_RAW:
                    if (chunk.DataProvider != null)
                    {
                        long offset = 0;
                        while (offset < totalBytes)
                        {
                            var toProcess = (int)Math.Min(buffer.Length, totalBytes - offset);
                            var read = chunk.DataProvider.Read(offset, buffer, 0, toProcess);
                            if (read <= 0)
                            {
                                break;
                            }

                            checksum = Crc32.Update(checksum, buffer, 0, read);
                            offset += read;
                        }
                    }
                    else
                    {
                        var zeroBuf = new byte[Math.Min(buffer.Length, totalBytes)];
                        long processed = 0;
                        while (processed < totalBytes)
                        {
                            var toProcess = (int)Math.Min(zeroBuf.Length, totalBytes - processed);
                            checksum = Crc32.Update(checksum, zeroBuf, 0, toProcess);
                            processed += toProcess;
                        }
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_FILL:
                    var fillValData = BitConverter.GetBytes(chunk.FillValue);
                    var fillBuf = new byte[Math.Min(buffer.Length, totalBytes)];
                    for (var i = 0; i < fillBuf.Length; i += 4)
                    {
                        Array.Copy(fillValData, 0, fillBuf, i, 4);
                    }
                    long processedFill = 0;
                    while (processedFill < totalBytes)
                    {
                        var toProcess = (int)Math.Min(fillBuf.Length, totalBytes - processedFill);
                        checksum = Crc32.Update(checksum, fillBuf, 0, toProcess);
                        processedFill += toProcess;
                    }
                    break;

                case SparseFormat.CHUNK_TYPE_DONT_CARE:
                    var zeroDontCareBuf = new byte[Math.Min(buffer.Length, totalBytes)];
                    long processedZero = 0;
                    while (processedZero < totalBytes)
                    {
                        var toProcess = (int)Math.Min(zeroDontCareBuf.Length, totalBytes - processedZero);
                        checksum = Crc32.Update(checksum, zeroDontCareBuf, 0, toProcess);
                        processedZero += toProcess;
                    }
                    break;
                default:
                    break;
            }
        }
        return Crc32.Finish(checksum);
    }

    private void MapChunks(SparseFile source, uint startBlock, uint blockCount)
    {
        uint currentSrcBlock = 0;
        var endBlock = startBlock + blockCount;

        foreach (var chunk in source.Chunks)
        {
            var chunkEnd = currentSrcBlock + chunk.Header.ChunkSize;

            if (chunkEnd > startBlock && currentSrcBlock < endBlock)
            {
                var intersectStart = Math.Max(startBlock, currentSrcBlock);
                var intersectEnd = Math.Min(endBlock, chunkEnd);
                var intersectCount = intersectEnd - intersectStart;

                var mappedChunk = CloneChunkSlice(chunk, intersectStart - currentSrcBlock, intersectCount);
                _mappedChunks.Add(mappedChunk);
            }

            currentSrcBlock = chunkEnd;
            if (currentSrcBlock >= endBlock)
            {
                break;
            }
        }
    }

    private SparseChunk CloneChunkSlice(SparseChunk original, uint offsetInBlocks, uint count)
    {
        var header = original.Header;
        header.ChunkSize = count;


        header.TotalSize = header.ChunkType == SparseFormat.CHUNK_TYPE_RAW
            ? SparseFormat.CHUNK_HEADER_SIZE + (count * _blockSize)
            : header.ChunkType == SparseFormat.CHUNK_TYPE_FILL ? SparseFormat.CHUNK_HEADER_SIZE + 4 : (uint)SparseFormat.CHUNK_HEADER_SIZE;

        var newChunk = new SparseChunk(header) { FillValue = original.FillValue };

        if (original.DataProvider != null && header.ChunkType == SparseFormat.CHUNK_TYPE_RAW)
        {
            newChunk.DataProvider = new SubDataProvider(original.DataProvider, (long)offsetInBlocks * _blockSize, (long)count * _blockSize);
        }

        return newChunk;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _totalByteLength)
        {
            return 0;
        }

        var totalRead = 0;
        while (totalRead < count && _position < _totalByteLength)
        {
            var section = FindSectionAtOffset(_position);
            var offsetInSection = _position - section.StartByteOffset;
            var toRead = (int)Math.Min(count - totalRead, section.Length - offsetInSection);

            switch (section.Type)
            {
                case SectionType.SparseHeader:
                case SectionType.ChunkHeader:
                case SectionType.CrcHeader:
                case SectionType.CrcData:
                    Buffer.BlockCopy(section.StaticData!, (int)offsetInSection, buffer, offset + totalRead, toRead);
                    break;

                case SectionType.ChunkData:
                    var chunk = _mappedChunks[section.ChunkIndex];
                    if (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_RAW)
                    {
                        chunk.DataProvider?.Read(offsetInSection, buffer, offset + totalRead, toRead);
                    }
                    else if (chunk.Header.ChunkType == SparseFormat.CHUNK_TYPE_FILL)
                    {
                        var fillValue = chunk.FillValue;
                        for (var i = 0; i < toRead; i++)
                        {
                            var byteIdx = (int)((offsetInSection + i) % 4);
                            buffer[offset + totalRead + i] = (byte)(fillValue >> (byteIdx * 8));
                        }
                    }
                    break;
                default:
                    break;
            }

            _position += toRead;
            totalRead += toRead;
        }

        return totalRead;
    }

    private Section FindSectionAtOffset(long pos)
    {
        int low = 0, high = _sections.Count - 1;
        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var sec = _sections[mid];
            if (pos >= sec.StartByteOffset && pos < sec.StartByteOffset + sec.Length)
            {
                return sec;
            }

            if (pos < sec.StartByteOffset)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        return _sections.Last();
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin: _position = offset; break;
            case SeekOrigin.Current: _position += offset; break;
            case SeekOrigin.End: _position = _totalByteLength + offset; break;
            default:
                break;
        }
        _position = Math.Clamp(_position, 0, _totalByteLength);
        return _position;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _totalByteLength;
    public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private class SubDataProvider(ISparseDataProvider parent, long offset, long length) : ISparseDataProvider
    {
        public long Length => length;
        public int Read(long inOffset, byte[] buffer, int bufferOffset, int count) =>
            parent.Read(offset + inOffset, buffer, bufferOffset, (int)Math.Min(count, length - inOffset));
        public void WriteTo(Stream stream) => throw new NotSupportedException();
        public void Dispose() { }
    }
}