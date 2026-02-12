using System.Buffers.Binary;

namespace LibSparseSharp;
/// <summary>
/// 将 SparseFile 包装为只读 Stream，允许随机访问
/// </summary>
public class SparseStream : Stream
{
    private readonly SparseFile _sparseFile;
    private readonly long _length;
    private long _position;
    private readonly (uint StartBlock, uint EndBlock, int ChunkIndex)[] _chunkLookup;

    public SparseStream(SparseFile sparseFile)
    {
        _sparseFile = sparseFile;
        _length = (long)sparseFile.Header.TotalBlocks * sparseFile.Header.BlockSize;

        // 构建查找表以加速随机访问 (Binary Search 准备)
        _chunkLookup = new (uint, uint, int)[sparseFile.Chunks.Count];
        uint currentBlock = 0;
        for (var i = 0; i < sparseFile.Chunks.Count; i++)
        {
            var numBlocks = sparseFile.Chunks[i].Header.ChunkSize;
            _chunkLookup[i] = (currentBlock, currentBlock + numBlocks, i);
            currentBlock += numBlocks;
        }
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override void Flush() { }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (_position >= _length)
        {
            return 0;
        }

        var toRead = (int)Math.Min(count, _length - _position);
        var totalRead = 0;
        Span<byte> fillValue = stackalloc byte[4];

        while (totalRead < toRead)
        {
            var (chunk, startBlock) = FindChunkAtOffset(_position);
            int currentReadSize;

            if (chunk == null)
            {
                // 如果在文件总长度范围内但没有 chunk 定义，视为 0（DONT_CARE 区域）
                // 找到下一个 chunk 之前的所有空间或者是到文件末尾
                var nextChunkBlock = GetNextChunkBlock(_position);
                var endOfGap = Math.Min(_length, (long)nextChunkBlock * _sparseFile.Header.BlockSize);
                currentReadSize = (int)Math.Min(toRead - totalRead, endOfGap - _position);

                if (currentReadSize <= 0)
                {
                    break;
                }

                Array.Clear(buffer, offset + totalRead, currentReadSize);

                _position += currentReadSize;
                totalRead += currentReadSize;
                continue;
            }

            var chunkStartOffset = (long)startBlock * _sparseFile.Header.BlockSize;
            var offsetInChunk = _position - chunkStartOffset;
            var chunkRemaining = ((long)chunk.Header.ChunkSize * _sparseFile.Header.BlockSize) - offsetInChunk;
            currentReadSize = (int)Math.Min(toRead - totalRead, chunkRemaining);

            ProcessChunkData(chunk, offsetInChunk, buffer, offset + totalRead, currentReadSize, fillValue);

            _position += currentReadSize;
            totalRead += currentReadSize;
        }

        return totalRead;
    }

    private uint GetNextChunkBlock(long position)
    {
        var targetBlock = (uint)(position / _sparseFile.Header.BlockSize);

        // 查找第一个 StartBlock > targetBlock 的 entry
        var low = 0;
        var high = _chunkLookup.Length - 1;
        var nextBlock = (uint)(_length / _sparseFile.Header.BlockSize);

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (_chunkLookup[mid].StartBlock > targetBlock)
            {
                nextBlock = _chunkLookup[mid].StartBlock;
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }
        return nextBlock;
    }

    private void ProcessChunkData(SparseChunk chunk, long offsetInChunk, byte[] buffer, int bufferOffset, int count, Span<byte> fillValue)
    {
        switch (chunk.Header.ChunkType)
        {
            case SparseFormat.CHUNK_TYPE_RAW:
                if (chunk.DataProvider != null)
                {
                    var read = chunk.DataProvider.Read(offsetInChunk, buffer, bufferOffset, count);
                    if (read < count)
                    {
                        Array.Clear(buffer, bufferOffset + read, count - read);
                    }
                }
                else
                {
                    Array.Clear(buffer, bufferOffset, count);
                }
                break;
            case SparseFormat.CHUNK_TYPE_FILL:
                BinaryPrimitives.WriteUInt32LittleEndian(fillValue, chunk.FillValue);
                for (var i = 0; i < count; i++)
                {
                    buffer[bufferOffset + i] = fillValue[(int)((offsetInChunk + i) % 4)];
                }
                break;
            default:
                Array.Clear(buffer, bufferOffset, count);
                break;
        }
    }

    private (SparseChunk? chunk, uint startBlock) FindChunkAtOffset(long offset)
    {
        var targetBlock = (uint)(offset / _sparseFile.Header.BlockSize);

        // 二分查找
        var low = 0;
        var high = _chunkLookup.Length - 1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            var (startBlock, endBlock, chunkIndex) = _chunkLookup[mid];

            if (targetBlock >= startBlock && targetBlock < endBlock)
            {
                return (_sparseFile.Chunks[chunkIndex], startBlock);
            }

            if (targetBlock < startBlock)
            {
                high = mid - 1;
            }
            else
            {
                low = mid + 1;
            }
        }

        return (null, 0);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        switch (origin)
        {
            case SeekOrigin.Begin: Position = offset; break;
            case SeekOrigin.Current: Position += offset; break;
            case SeekOrigin.End: Position = _length + offset; break;
            default:
                break;
        }
        return Position;
    }

    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}